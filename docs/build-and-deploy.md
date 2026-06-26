# ビルドと配置

`ai-harness-main`（daemon／standalone 本体）と `ai-harness-client`（hook クライアント）のビルド・発行・配置手順。Windows / Linux 双方を扱う。

## 前提

- .NET SDK 10.0 以降
- ターゲットフレームワーク: `net10.0`

```sh
dotnet --version   # 10.0 以上を確認
```

## 開発ビルド（動作確認用）

ソリューションは無いため、プロジェクト単位でビルドする。`ai-harness-main` は `ai-harness-baselib` を `ProjectReference` するため、baselib も同時にビルドされる。

```sh
# Linux / macOS
dotnet build ai-harness-main/ai-harness-main/ai-harness-main.csproj -c Release
```

```powershell
# Windows (PowerShell)
dotnet build ai-harness-main\ai-harness-main\ai-harness-main.csproj -c Release
```

## 本番発行（self-contained 単一ファイル）

配置先に .NET ランタイムを要求しないよう、自己完結・単一ファイルで発行する。`-r` で対象 RID を指定する。

### Linux 向け

```sh
dotnet publish ai-harness-main/ai-harness-main/ai-harness-main.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o <配置先>

dotnet publish ai-harness-client/ai-harness-client/ai-harness-client.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o <配置先>
```

### Windows 向け

```powershell
dotnet publish ai-harness-main\ai-harness-main\ai-harness-main.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o <配置先>

dotnet publish ai-harness-client\ai-harness-client\ai-harness-client.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o <配置先>
```

### RID 早見表

| OS / アーキテクチャ | RID |
|---|---|
| Linux x64 | `linux-x64` |
| Linux ARM64 | `linux-arm64` |
| Windows x64 | `win-x64` |
| Windows ARM64 | `win-arm64` |
| macOS Apple Silicon | `osx-arm64` |
| macOS Intel | `osx-x64` |

クロス発行が可能（Linux 上から `-r win-x64` で Windows 向け発行など）。`ai-harness-main` と `ai-harness-client` は配置先 OS に合わせて同じ RID で発行する。

## 配置レイアウト

`ai-harness-main` と `ai-harness-client` は**同一ディレクトリに同居**させる。client が同居の daemon を起動し、同じパイプ名（実行体ディレクトリの SHA256）を算出するため。

```
<配置先>/                     例: .claude/harness/
├── ai-harness-client(.exe)   hook が叩く薄いクライアント
├── ai-harness-main(.exe)     daemon／standalone 本体
├── lib/                      拡張プラグイン（*.dll）
├── config/                   設定（main.yml ＋ 各プラグインの YAML）
├── logs/                     統合ログ（<yyyy-MM-dd>.jsonl、自動生成）
└── run/                      daemon 作業領域（daemon.lock 等、自動生成）
```

```sh
# 設定の既定値を配置（初回）
mkdir -p <配置先>/config <配置先>/lib
cp ai-harness-main/ai-harness-main/config/main.yml <配置先>/config/

# プラグインを配置
cp <plugin>.dll        <配置先>/lib/
cp <plugin>.yml        <配置先>/config/
```

> `lib/` には**プラグイン DLL のみ**を置く。`ai-harness-baselib.dll` は host が共有ロードするため置かない（プラグインの csproj 側で `<Private>false</Private>` と `CopyLocalLockFileAssemblies=false` を指定して出力から除外する）。loader も `ai-harness-baselib` という名前の DLL はスキップする。

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

## daemon の制御

| コマンド | 用途 |
|---|---|
| `ai-harness-main --ensure` | 未起動なら detached 起動（SessionStart hook 等から） |
| `ai-harness-main --restart` | プラグイン DLL・config の変更を反映（停止→再起動） |
| `ai-harness-main --stop` | 停止 |

DLL や設定を差し替えたら `--restart` で反映する。何もしなければ idle 30 分で daemon は自動終了する。

## 既知の制約

- **多重起動抑止が best-effort**: Unix では `FileShare.None` ロックが cross-process 強制されないため、稀に daemon が多重起動し得る。`--ensure`／client のパイプ疎通チェックで大半は回避。
- **ゾンビプロセス**: detached daemon の終了後、PID1 が reap しない環境（非 systemd の最小コンテナ等）で defunct 化し得る。無害。実機 init では回収される。
