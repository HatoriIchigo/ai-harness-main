# 更新と自己更新（開発者向け）

`--update` の内部設計。プラグインの一括インストールと、本体（`ai-harness-main` 自身）の自己更新を 1 コマンドで扱う。運用手順は [build-and-deploy.md](build-and-deploy.md)、設定ファイルは [configuration.md](configuration.md) を参照。ここでは**なぜこの構造か・どう実装したか**を示す。

## 全体像

`ai-harness-main --update` は 2 段で動く。

```
--update（稼働中の実行体）
  1. プラグイン更新（同期）     repos/ へ clone/pull → dotnet build → lib/ へ配置
  2. 本体自己更新（ハンドオフ） self+baselib を tmp へ clone → publish → 新バイナリへ制御を渡す
        └─▶ --apply-update（tmp の新バイナリ）
              旧プロセス終了待ち → daemon 停止 → 実行体を .bak 退避 → 上書き → --health 検証
                                → 失敗ならロールバック → daemon 再起動 → tmp 掃除
```

取得元は本体直下 `config/plugins.yml` に集約する（`self` / `baselib` / `plugins` の 3 節）。

## なぜ 2 プロセスに分けるか

**稼働中の実行ファイルは自分自身では置換できない。** 特に Windows は実行中 exe がロックされ上書き不可。Linux は rename で置換できるが、挙動を揃えるため両 OS ともハンドオフ方式に統一する。

置換を行う主体（applier）を「**置換対象 exe とは別ファイル**」にすればこの制約を回避できる。そこで publish 済みの**新バイナリ**（`tmp/out/ai-harness-main`）を `--apply-update` モードで起動し、そちらにインストール先 exe の置換を任せる。`--update` を実行していた旧プロセスはハンドオフ後すぐ終了し、exe のロックを解放する。

applier は bat/sh ではなく**本体と同一コードベースの内部モード**（`Program.cs` の `--apply-update`）。ヘルパの改良が本体と一緒に進み、C# のまま書ける。

## `SelfUpdater.Run`（稼働中の実行体側）

`PluginInstaller.Run` がプラグイン更新後に呼ぶ。戻り値 `true` = applier へハンドオフ済み。

1. **対象 exe の特定** — `Environment.ProcessPath`。`dotnet <dll>` 経由起動（ミュクサ）では置換すべき本体を特定できないため**スキップして `false`**。self-contained 単一ファイル発行の実行体でのみ自己更新する。
2. **tmp へ取得** — `self` と `baselib` を tmp の**兄弟ディレクトリ**へ clone。本体 csproj も baselib を `..\..\ai-harness-baselib\...` と相対参照するため、この兄弟レイアウトが必須（プラグインと同じ理由）。
3. **publish** — `dotnet publish -c Release -r <現在の RID> --self-contained true -p:PublishSingleFile=true` で `tmp/out` へ。配置先に .NET を要求しない現行方針に合わせる。
4. **発行直後の健全性検証** — `tmp/out` の新バイナリを `--health` で起動確認。ここで落ちれば置換に**進まない**（壊れた本体を配らない第一の関門）。
5. **ハンドオフ** — 新バイナリを `--apply-update --target <install exe> --pid <自分> --tmp <作業領域>` で detached 起動し、`Run` は `true` を返す。呼び出し元の `--update` はそのまま終了（ロック解放）。

## `SelfUpdater.ApplyUpdate`（tmp の新バイナリ側）

`--apply-update` で起動。インストール先の実行体を安全に置換する。ログは**インストール先の `logs/`**（グローバルログ）へ出す（自分の隣ではなく置換対象基準）。

1. **旧プロセス終了待ち** — `--pid` のプロセスを最大 30 秒待つ（exe ロック解放のため）。
2. **daemon 停止** — 停止シグナルを送り、完全停止（ロック解放）まで待つ。パイプ名は**インストール先ディレクトリ基準**で算出する（applier は tmp から動くため `HarnessPipe.NameFor(installDir)` を使う。`AppContext.BaseDirectory` 基準の `Name()` では別物になる）。
3. **退避** — 現行実行体を `<exe>.bak` へコピー。
4. **上書き** — 新バイナリを `--target` へコピー。ロック解放が間に合わない場合に備え、`IOException` / `UnauthorizedAccessException` を最大 30 秒**リトライ**する。
5. **置換後検証** — `<target> --health`。失敗なら `.bak` から**ロールバック**し、非 0 で終了。
6. **仕上げ** — 成功なら `.bak` を削除。daemon が動いていたなら `<target> --ensure` で再起動（新 `lib` と新バイナリを反映）。tmp を掃除（自分自身の exe を含むため Windows では消えないことがあり best-effort）。

## フェイルセーフ設計

本体が壊れると、フェイルクローズ方針ゆえ**全 hook がロックアウト**する（bridge が daemon に繋げないときのみ許可継続だが、壊れた exe はそれ以前に起動しない）。二重の関門で壊れた本体の配布を防ぐ。

| 関門 | 位置 | 落ちたら |
|---|---|---|
| 発行直後の `--health` | `Run`（置換前） | 置換に進まない。旧本体のまま |
| 置換後の `--health` | `ApplyUpdate`（置換後） | `.bak` へロールバック。旧本体へ復帰 |

`ApplyUpdate` は途中失敗（コピー例外等）でも `.bak` からの復元を試みる。復元も失敗した場合はログに記録する（この時点で手動復旧が必要）。

## 同期性の制約（Windows）

置換は「旧プロセス終了後」に別プロセスで進むため、**置換の成否は `--update` の終了コードに載らない**。`--update` はハンドオフした時点で 0 を返し「バックグラウンドで適用中。結果は logs/ を参照」と表示する。結果はインストール先 `logs/<date>.jsonl` の以下で確認する。

- `自己更新 apply 開始 target=...`
- `実行体を置換。起動検証中。`
- `自己更新に成功。` / `置換後の起動検証に失敗。旧実行体へロールバックした。`

Linux は rename での同期置換も可能だが、OS 差をなくすため両者ともこの非同期ハンドオフで統一している。

## スキップ条件と前提

- **`git` / `dotnet` 未導入** — `--update` はプラグイン段階で異常終了（非 0）。自己更新まで到達しない。
- **`dotnet <dll>` 経由起動** — 本体 exe を特定できず自己更新をスキップ（プラグイン更新は実施）。開発ビルドでは基本これ。
- **SHA 埋め込みなし** — 現状、本体が同一でも `--update` のたびに再 publish → 置換 → 再起動する。無駄 swap を避けたい場合は、ビルド時に git SHA を埋め込み `--health` で出力し、fetch した SHA と一致ならスキップする拡張余地がある（未実装）。

## 関連する実行モード

| モード | 公開 | 用途 |
|---|---|---|
| `--update` | ユーザ | プラグイン更新 ＋ 本体自己更新 |
| `--apply-update` | 内部 | tmp の新バイナリが実行体を置換（`--update` が起動） |
| `--health` | 検証用 | ランタイムが正常起動すれば 0。置換の健全性判定に使う |

## 関連コード

| ファイル | 役割 |
|---|---|
| `Install/SelfUpdater.cs` | 自己更新本体（`Run` / `ApplyUpdate`） |
| `Install/PluginInstaller.cs` | `--update` 実体。プラグイン更新後に `SelfUpdater.Run` を呼ぶ |
| `Install/PluginsConfig.cs` | `plugins.yml`（`self` / `baselib` / `plugins`）のロード |
| `Ipc/HarnessPipe.cs` | `NameFor(baseDir)` でインストール先基準のパイプ名を算出 |
| `Ipc/Daemon.cs` | `Stop(pipeName)` / `IsRunning(pipeName)` にパイプ名指定を追加 |
| `Program.cs` | `--update` / `--apply-update` / `--health` の分岐 |
