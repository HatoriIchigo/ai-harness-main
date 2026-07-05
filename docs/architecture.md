# アーキテクチャ

ai-harness-main の内部構成と処理フロー。コンポーネント単位の責務と、リクエストがどう流れるかを示す。

## 全体像

単一バイナリ `ai-harness-main` が bridge（hook 受け口）と daemon（常駐サーバー）を兼ねる。
プラグインとコアは `ai-harness-baselib` の契約のみを共有し、互いを直接参照しない。

```
Claude Code ──hook──▶ ai-harness-main (bridge) ──named pipe──▶ ai-harness-main (daemon)
  プロジェクト A          cwd→ルート解決し封筒で中継            │（単一・共有）
  プロジェクト B  ──────────────────────────────────────────▶ │
                                                              ├─ lib/*.dll          共有プラグイン（型発見1回）
                                                              ├─ ProjectContext A   A の設定・ログ・有効プラグイン
                                                              ├─ ProjectContext B   B の設定・ログ・有効プラグイン
                                                              └─ ai-harness-baselib  共有契約

ai-harness-main  ──┐
                   ├──▶  ai-harness-baselib
各プラグイン DLL  ──┘
```

- **bridge** … hook が叩く受け口。cwd からプロジェクトルートを解決し、hook JSON を封筒で daemon へ中継。未起動なら daemon を自動起動。
- **daemon** … 単一・共有の常駐サーバー。型発見（1回）を保持し、プロジェクトルートをキャッシュキーにプロジェクト別へ振り分ける。
- **baselib** … `PluginBase` / `HookData` 等、境界を越える型のみを定義。

## 実行モード

`Program.cs` が第1引数で分岐する。

| 起動 | 役割 |
|---|---|
| （引数なし） | bridge。hook ごとに起動され、stdin の hook JSON を daemon へ中継。未起動なら daemon を detached 起動 |
| `--daemon` | 常駐サーバー。名前付きパイプで接続を待ち受け、プロジェクト別に処理 |
| `--ensure` | daemon 未起動なら detached 起動して終了 |
| `--stop` | 稼働中の daemon を停止 |
| `--restart` | daemon を停止後に再起動（`lib` のプラグイン DLL 差し替え反映用） |
| `--standalone` | daemon を介さず stdin の hook JSON を 1 件処理して終了（テスト・フォールバック） |

## コンポーネント

ソースは責務別ディレクトリに分割。namespace は全て `ai_harness_main`（フラット）。

| ファイル | 責務 |
|---|---|
| `Program.cs` | エントリ。モード分岐と standalone 処理 |
| `Bridge/Bridge.cs` | bridge モード。cwd→ルート解決、封筒生成、daemon へ送信、応答出力、未起動時の起動 |
| `Bridge/ProjectLocator.cs` | cwd から `.claude` を上方探索しプロジェクトルートを決定 |
| `Plugins/PluginLoader.cs` | `lib/*.dll` を走査し `PluginBase` 派生型を発見 |
| `Plugins/PluginRegistry.cs` | 共有型レジストリ。起動時 1 回の型発見を保持（全プロジェクト共有） |
| `Plugins/ProjectContext.cs` | プロジェクト別の検証・`Init`・発火・設定ホットリロード・最終アクセス管理 |
| `Plugins/PluginHost.cs` | リクエスト毎にインスタンス生成・並列発火・deny 先勝ち集約 |
| `Plugins/PluginLoadContext.cs` | プラグイン DLL 用 ALC。baselib は既定 ALC へ委ね型同一性を保つ |
| `Ipc/Daemon.cs` | 常駐サーバ。マルチテナント振り分け・アイドル回収・自動停止・ensure／stop／restart |
| `Ipc/RequestEnvelope.cs` | bridge→daemon の封筒（`type`／`projectRoot`／`hookJson`） |
| `Ipc/HarnessPipe.cs` | パイプ名生成（実行体ディレクトリの SHA256） |
| `Ipc/HookOutput.cs` | Claude へ返す hook 出力 JSON（`hookSpecificOutput.additionalContext`）の組み立て |
| `Ipc/HookResponse.cs` | daemon→bridge の応答ペイロード |
| `Ipc/Framing.cs` | 長さ前置フレーミング |
| `Config/InstallPaths.cs` | 実行体基準のグローバルパス（`lib`／`run`／グローバル log） |
| `Config/ProjectConfig.cs` | プロジェクト個別設定（`<ルート>/.claude/harness/config/common.yml` ロード） |
| `Logging/Logger.cs` | レベルフィルタ＋ログファイルへ集約（出力先ディレクトリは引数） |

## マルチテナント

単一の daemon が複数プロジェクトをさばく。`lib`（プラグイン型）は全プロジェクト共有、設定・ログ・有効プラグイン集合はプロジェクトごとに分離する。

- **型発見**: daemon 起動時に `lib/` を 1 回走査し、`PluginRegistry` が型一覧を保持。
- **プロジェクト識別**: bridge が自プロセスの cwd からルート（`.claude` を含む階層）を解決し、封筒に詰めて渡す。daemon は常駐ゆえ各 hook の cwd を持たないため、識別は bridge が担う。
- **キャッシュ**: daemon はルートをキーに `ProjectContext` を遅延生成・キャッシュ（`Lazy` で二重生成を防止）。初回リクエストでそのプロジェクトの `common.yml`（有効化トグル）と各プラグイン YAML（個別設定）を読み、検証・`Init` を済ませる。

## プラグインのライフサイクル

型発見（重い処理）はプロセス寿命で1回・全プロジェクト共有。検証・`Init`・発火はプロジェクトごと。発火はリクエスト毎にインスタンスを作り直す（隔離維持・ステートレス前提）。

```
[daemon 起動時 = プロセス寿命で1回]
  PluginLoader.DiscoverTypes(lib/)      … *.dll 走査して PluginBase 派生型を収集
        ▼
  PluginRegistry                        … 型一覧を保持（全プロジェクト共有）

[プロジェクト初回アクセス時 = ルートごとに1回]
  ProjectContext.Create(types, root)    … 型ごとに1インスタンス生成し:
        ├─ common.yml の tools で有効化されたもののみ対象
        ├─ Tools / Events を検証（NG なら除外）
        ├─ LoadConfig(configDir)（ConfigName 必須。失敗で除外）
        └─ Init() を1回実行しログ
        ▼
  有効プラグイン型のリストを保持＋設定 YAML を FileSystemWatcher で監視

[hook 発火時 = リクエスト毎]
  PluginHost.RunAsync(types, data)
        ├─ 型ごとに新インスタンスを生成（隔離）
        ├─ ShouldFire(data) で発火判定（OR 評価）
        ├─ LoadConfig(configDir)（Action から Config 参照可に）
        ├─ Action(data, result) を列挙 → result.ExitCode 確定
        └─ 全結果を Aggregate（deny 先勝ち）
```

- `Init` を実行するインスタンスと `Action` のインスタンスは**別**。状態は持ち越さない。
- プラグインのクラッシュ（`LoadConfig`／`Action` の例外）や生成失敗は**フェイルクローズ**（当該プラグインを deny 扱いにしてブロック）。検証を完了できないアクションを素通りさせないため。
- `Tools`/`Events`/`FileNames`/`BashCommands` が全て `null` のプラグインは発火条件が無く、除外はされないが一切発火しない（起動時に警告ログ）。

## ホットリロード

各 `ProjectContext` は自身の設定ディレクトリ（`<ルート>/.claude/harness/config`）の `*.yml` を `FileSystemWatcher` で監視する。

- `common.yml`・各プラグイン YAML の変更を検知し、デバウンス（連続イベントを束ねる）後に当該プロジェクトの有効プラグイン集合を再構築（設定再ロード・`Init` 再実行）。
- 再構築結果は原子的に差し替え、実行中リクエストは差し替え前の集合を使い続ける。
- `lib` の DLL 差し替えはホットリロード対象外。`--restart` で型レジストリごと作り直す。

## アイドル回収と自動停止

Claude Code 終了後に daemon が居座らないよう、2 段で回収する。

- **プロジェクト回収**: スイーパが定期走査し、無アクセスが一定時間（既定 5 分）を超えたプロジェクトの `ProjectContext` を破棄（`FileSystemWatcher` も dispose）。
- **自動停止**: 全プロジェクトが回収されメモリ（キャッシュ）が空になった時点で daemon 自体を終了。加えて、接続が一定時間（既定 5 分）全く無ければ受付ループのアイドルで終了する。

## リクエスト処理フロー

1. Claude Code の hook → `ai-harness-main`（bridge）を起動
2. bridge が cwd から `.claude` を上方探索してプロジェクトルートを解決
3. bridge が名前付きパイプで daemon へ接続（未起動なら `--daemon` を detached 起動して再接続）
4. bridge が封筒 `{ type, projectRoot, hookJson }` をフレーム送信
5. daemon が `projectRoot` で `ProjectContext` を取得（初回は生成）、`HookData.Parse` で解析し `PluginHost` が全プラグインを並列発火（`maxParallel` で同時数制限）
6. 各 `PluginResult` を **deny 先勝ち**で集約（1 つでも `ExitCode != 0` なら全体 deny、理由を改行連結）
7. daemon が `{ exitCode, reason, additionalContext }` を応答 → bridge が deny 理由を stderr、additionalContext を hook 出力 JSON で stdout、その exit code で終了

## 終了コード

bridge（hook）が Claude Code へ返す終了コードは 2 値。

| コード | 意味 |
|---|---|
| `0` | 許可（正常）。**例外的に** bridge が daemon にまったく接続できない（基盤ごと停止）ときも、ロックアウト回避のため 0（fail-open） |
| `2` | deny（ブロック）。理由を stderr へ。プラグインによる deny のほか、**内部エラー・不正入力・`common.yml` 不正など「検証できなかった」場合のフェイルクローズもここに倒す** |

daemon 経路・`--standalone` とも同じ方針（内部エラーは 2＝ブロック）。唯一 fail-open で 0 を返すのは、bridge が daemon に接続できないケースのみ。

## IPC（プロセス間通信）

- **トランスポート**: 名前付きパイプ。Windows＝ネイティブ名前付きパイプ、Unix＝UDS 実装。コードは単一（OS 差は .NET が吸収）。
- **パイプ名**: 実行体ディレクトリ（`AppContext.BaseDirectory`）の SHA256 先頭 16 hex から決定的に生成（`ai-harness-<hash>`）。bridge と daemon は同一実行体から起動されるため同名を算出し、インストール単位で 1 つの共有 daemon に対応する。
- **フレーミング**: 長さ前置（int32 LE）。1 フレーム上限 64 MiB。
- **リクエスト**: `RequestEnvelope`（UTF8 JSON）。`type=hook` は `projectRoot` ＋ `hookJson`、`type=stop` は停止要求。
- **応答**: `HookResponse`（UTF8 JSON）。`exitCode` ＋ `reason` ＋ `additionalContext`。

## 多重起動抑止

`run/daemon.lock` を `FileShare.None` で開いて排他する（best-effort）。`--ensure` と bridge のパイプ疎通チェックで大半の競合を回避する。

## 設定の反映タイミング

- **設定 YAML（`common.yml`・各プラグイン YAML）**: ホットリロード対象。保存すると当該プロジェクトが再構築される。
- **プラグイン DLL（`lib/`）**: daemon 起動時に 1 回読む。差し替え反映には `--restart` が必要。

詳細は [configuration.md](configuration.md) を参照。
