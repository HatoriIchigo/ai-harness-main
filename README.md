# ai-harness-main

> Claude Code の hook を受け、拡張プラグインを発火させる常駐ハーネス本体。

`ai-harness-client` からの hook を名前付きパイプで受け、`lib/` に置かれた拡張プラグイン（`PluginBase` 派生 DLL）を並列発火し、結果を **deny 先勝ち**で集約して Claude Code へ返す。プラグインのロードはプロセス寿命で 1 回、発火はリクエスト毎にインスタンスを作り直して隔離する。

## 特徴

- **常駐（daemon）でウォーム保持** — プラグインのロード・型発見は起動時 1 回。hook ごとの遅延を抑える。
- **プラグイン分離** — コア（`ai-harness-main`）と拡張は `ai-harness-baselib` の契約のみを共有。依存は一方向。
- **deny 先勝ち集約** — 1 つでも非 0 を返せば全体 deny。理由を Claude Code へ返す。
- **フェイルオープン** — プラグインのクラッシュや daemon 接続不能はブロックしない（許可で継続）。
- **クロスプラットフォーム** — Windows／Linux を単一コードで対応（IPC の OS 差は .NET が吸収）。

## クイックスタート

```sh
# 1. self-contained 単一ファイルで発行（Linux の例）
dotnet publish ai-harness-main/ai-harness-main/ai-harness-main.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <配置先>

# 2. client も同じ配置先へ発行（同居が必須）
dotnet publish ai-harness-client/ai-harness-client/ai-harness-client.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <配置先>

# 3. 設定とプラグインを配置
mkdir -p <配置先>/config <配置先>/lib
cp ai-harness-main/ai-harness-main/config/main.yml <配置先>/config/
cp <plugin>.dll <配置先>/lib/
cp <plugin>.yml <配置先>/config/
```

Claude Code の `settings.json` では hook コマンドに **client** を指定する。

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "\"$CLAUDE_PROJECT_DIR/.claude/harness/ai-harness-client\"" } ] }
    ]
  }
}
```

詳細は [docs/build-and-deploy.md](docs/build-and-deploy.md) を参照。

## 実行モード

| 起動 | 役割 |
|---|---|
| （引数なし） | standalone。stdin の hook JSON を 1 件処理して終了 |
| `--daemon` | 常駐。パイプで接続を待ち受ける。idle 30 分で自動終了 |
| `--ensure` | 未起動なら detached 起動 |
| `--restart` | 停止→再起動（プラグイン DLL・config の変更反映） |
| `--stop` | 停止 |

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [docs/architecture.md](docs/architecture.md) | 全体構成・コンポーネント・処理フロー・IPC・ライフサイクル |
| [docs/plugin-development.md](docs/plugin-development.md) | 新規プラグイン作成の手順（csproj・実装・発火条件・配置） |
| [docs/build-and-deploy.md](docs/build-and-deploy.md) | Windows／Linux のビルド・発行・配置・daemon 制御 |
| [docs/configuration.md](docs/configuration.md) | `main.yml`・プラグイン設定・ディレクトリ規約・ログ |

## プロジェクト構成

```
ai-harness-main/
├── README.md
├── docs/                       ドキュメント
└── ai-harness-main/
    ├── ai-harness-main.csproj
    ├── Program.cs              エントリ（モード分岐・standalone 処理）
    ├── HarnessCore.cs          中核（型発見・検証・Init・リクエスト入口）
    ├── Plugins/
    │   ├── PluginLoader.cs      DLL 走査・型発見
    │   ├── PluginHost.cs        リクエスト毎の発火・deny 集約
    │   └── PluginLoadContext.cs プラグイン DLL 用 ALC（baselib 共有）
    ├── Ipc/
    │   ├── Daemon.cs            常駐サーバ・ensure／stop／restart
    │   ├── HarnessPipe.cs       パイプ名生成
    │   └── Framing.cs           長さ前置フレーミング
    ├── Config/
    │   └── HarnessConfig.cs     固定ディレクトリ＋main.yml ロード
    ├── Logging/
    │   └── Logger.cs            レベルフィルタ＋単一ログ集約
    └── config/
        └── main.yml             設定の既定値（配置元）
```

## 関連プロジェクト

| プロジェクト | 役割 |
|---|---|
| `ai-harness-baselib` | プラグイン契約（`PluginBase`／`HookData` 等）を定義する共有ライブラリ |
| `ai-harness-client` | hook が実際に叩く薄いクライアント。stdin を daemon へ中継 |
| `sample-plugins/` | 動作する参考プラグイン（`EventLogger`／`DenyMarker`／`LogTester`） |
