# ビルドと配置

単一バイナリ `ai-harness-main`（bridge／daemon／standalone を兼ねる）のビルド・発行・配置手順。Windows / Linux 双方を扱う。

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
  -o <インストール先>
```

### Windows 向け

```powershell
dotnet publish ai-harness-main\ai-harness-main\ai-harness-main.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o <インストール先>
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

クロス発行が可能（Linux 上から `-r win-x64` で Windows 向け発行など）。配置先 OS に合わせた RID で発行する。

## 配置レイアウト

インストール先に実行体と共有プラグインを置く。`ai-harness-main` を **PATH 解決**できるようにする（Linux は `/usr/local/bin` への symlink、Windows は PATH へ追加）。設定とログは各プロジェクトの `.claude/harness/` 配下に分かれる。

```
<インストール先>/               実行体と共有資産（全プロジェクト共通）
├── ai-harness-main(.exe)       単一バイナリ（bridge／daemon／standalone）
├── lib/                        共有プラグイン（*.dll）
├── run/                        daemon 作業領域（daemon.lock、自動生成）
└── logs/                       daemon ライフサイクルログ（自動生成）

<プロジェクトルート>/.claude/harness/
├── config/                     設定（common.yml ＋ 各プラグインの YAML）
└── logs/                       hook 処理ログ（<yyyy-MM-dd>.jsonl、自動生成）
```

```sh
# PATH 解決（Linux）
ln -sf <インストール先>/ai-harness-main /usr/local/bin/ai-harness-main

# 共有プラグインを配置
mkdir -p <インストール先>/lib
cp <plugin>.dll <インストール先>/lib/

# プロジェクト側の設定を配置
mkdir -p <プロジェクト>/.claude/harness/config
cp ai-harness-main/ai-harness-main/config/common.yml <プロジェクト>/.claude/harness/config/
cp <plugin>.yml                                       <プロジェクト>/.claude/harness/config/
```

> `lib/` には**プラグイン DLL のみ**を置く。`ai-harness-baselib.dll` は host が共有ロードするため置かない（プラグインの csproj 側で `<Private>false</Private>` と `CopyLocalLockFileAssemblies=false` を指定して出力から除外する）。loader も `ai-harness-baselib` という名前の DLL はスキップする。

## Claude Code への配線（settings.json）

hook コマンドは **`ai-harness-main`** を指定する（PATH 解決・環境変数不要）。bridge として cwd からプロジェクトを解決し、daemon へ中継する。

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "ai-harness-main" } ] }
    ]
  }
}
```

## daemon の制御

| コマンド | 用途 |
|---|---|
| `ai-harness-main --ensure` | 未起動なら detached 起動 |
| `ai-harness-main --restart` | `lib` のプラグイン DLL 差し替えを反映（停止→再起動） |
| `ai-harness-main --stop` | 停止 |

- 設定 YAML（`common.yml`・各プラグイン YAML）はホットリロードされるため、`--restart` は不要。DLL を差し替えたときだけ `--restart`。
- 何も操作しなくても、全プロジェクトが回収されメモリが空になるか、接続が 5 分途絶えれば daemon は自動終了する。

## 既知の制約

- **多重起動抑止が best-effort**: Unix では `FileShare.None` ロックが cross-process 強制されないため、稀に daemon が多重起動し得る。`--ensure`／bridge のパイプ疎通チェックで大半は回避。
- **ゾンビプロセス**: detached daemon の終了後、PID1 が reap しない環境（非 systemd の最小コンテナ等）で defunct 化し得る。無害。実機 init では回収される。
