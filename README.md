# ai-harness-main

## 概要

AI ハーネスのメインプログラム。**常駐（daemon）**してプラグインをウォームに保ち、hook ごとに `ai-harness-client` からの接続を名前付きパイプで受けて処理する。

プラグインのロード・走査はプロセス寿命で1回。リクエスト毎にインスタンスを生成して発火する（隔離維持）。ディレクトリは実行体相対で固定。

## 実行モード

| 起動 | 役割 |
|---|---|
| （引数なし） | standalone。stdin の hook JSON を1件処理して終了（直接実行・テスト・フォールバック用） |
| `--daemon` | 常駐。名前付きパイプで接続を待ち受け、接続ごとに処理。idle 30分で自動終了 |
| `--ensure` | daemon 未起動なら detached 起動して終了 |
| `--stop` | 稼働中の daemon を停止 |
| `--restart` | 稼働中の daemon を停止して再起動（プラグイン DLL・config の変更反映用） |

hook の実体は `ai-harness-client`（別プロジェクト）。client が stdin を daemon へ中継し、未起動なら daemon を自動起動する。

## ディレクトリ構成（配置先）

```
.claude/harness/
  |- ai-harness-client      hook が叩く薄いクライアント
  |- ai-harness-main        daemon／standalone 本体
  |- lib/                   拡張プラグイン（*.dll）
  |- config/                設定（main.yml）
  |- logs/                  統合ログ（<yyyy-MM-dd>.jsonl）
  |- run/                   daemon 作業領域（ロック等）
```

## 処理フロー

1. Claude Code の hook → `ai-harness-client` を起動
2. client が名前付きパイプで daemon へ接続（未起動なら `--daemon` を detached 起動して再接続）
3. client が stdin の hook JSON を送信
4. daemon が `HookData` へ解析し、全プラグインを並列発火（リクエスト毎にインスタンス生成）
5. `PluginResult` を **deny 先勝ち**で集約
6. daemon が `{exitCode, reason}` を応答 → client が reason を stderr、exitCode で終了

## 終了コード（client）

| コード | 意味 |
|---|---|
| `0` | 許可（正常／daemon 接続不能時の fail-open も 0） |
| `2` | deny（ブロック）。理由を stderr へ |

## 通信

- 名前付きパイプ。名前は実行体ディレクトリの SHA256 から決定的に生成（プロジェクト単位で分離）
- 長さ前置（int32 LE）フレーミング。応答 = `int32 exitCode + UTF8 reason`
- Windows＝ネイティブ名前付きパイプ、Unix＝UDS 実装。コードは単一（OS 差は .NET が吸収）

## 設定（config/main.yml）

```yaml
logLevel: Info     # Trace / Debug / Info / Warning / Error
maxParallel: 0     # 0 で論理プロセッサ数
```

ファイル欠落・破損時は既定値。daemon 起動時に読むため、変更反映には daemon 再起動（`--restart`）が必要。プラグイン DLL（`lib/`）の差し替えも同様に `--restart` で反映する。

## ログ

全ログ（claude＝ハーネス由来 / 各プラグイン）を単一ファイル `logs/<yyyy-MM-dd>.jsonl` に集約（1 行 1 JSON：`timestamp`/`level`/`source`/`message`）。daemon 時は stderr へ出さずファイルのみ。

## ビルド・配置

```sh
# daemon／standalone（自己完結・単一ファイル）
dotnet publish ai-harness-main/ai-harness-main/ai-harness-main.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <配置先>

# client（同上。win-x64 等へクロス発行も可）
dotnet publish ai-harness-client/ai-harness-client/ai-harness-client.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <配置先>

cp ai-harness-main/ai-harness-main/config/main.yml <配置先>/config/
cp <plugin>.dll <配置先>/lib/
```

`ai-harness-main` と `ai-harness-client` は同一ディレクトリに配置する（client が同居の daemon を起動し、同じパイプ名を算出するため）。

## Claude Code への配線（settings.json）

hook コマンドは **client** を指定する（環境変数不要）。

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "\"$CLAUDE_PROJECT_DIR/.claude/harness/ai-harness-client\"" } ] }
    ]
  }
}
```

## 構成（ソース）

```
ai-harness-main/ai-harness-main/
├── Program.cs              エントリ（モード分岐）
├── Daemon.cs              常駐サーバ・ensure・stop・多重起動抑止
├── HarnessCore.cs         プラグイン型発見(1回)＋リクエスト処理
├── PluginLoader.cs        DLL 走査・型発見（ウォーム）
├── PluginHost.cs          リクエスト毎インスタンス生成・並列発火・deny集約
├── PluginLoadContext.cs   プラグイン DLL 用 ALC（baselib 共有）
├── HarnessConfig.cs       固定ディレクトリ＋main.yml ロード
├── Logger.cs              レベルフィルタ＋単一ログ集約
├── HarnessPipe.cs         パイプ名生成
├── Framing.cs             長さ前置フレーミング
└── config/main.yml        設定の既定値（配置元）
```

## 既知の制約

- **多重起動抑止が best-effort**: Unix では FileShare.None ロックが cross-process 強制されないため、稀に daemon が多重起動し得る。`--ensure`／client のパイプ疎通チェックで大半は回避。堅牢化は今後（flock / named mutex）。
- **ゾンビ**: detached daemon の終了後、PID1 が reap しない環境（非 systemd の最小コンテナ等）で defunct 化。無害。実機 init では回収される。

