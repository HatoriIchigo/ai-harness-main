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
├── lib/                        共有プラグイン（*.dll。マネージド依存 TreeSitter.dll 等も同居）
├── runtimes/                   tree-sitter ネイティブ grammar（<rid>/native/*.dll）。host が起動時に事前ロード
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

> `lib/` には**プラグインの管理 DLL のみ**を置く。`ai-harness-baselib.dll` は host が共有ロードするため置かない（プラグインの csproj 側で `<Private>false</Private>` と `CopyLocalLockFileAssemblies=false` を指定して出力から除外する）。loader も `ai-harness-baselib` という名前の DLL はスキップする。プラグインが参照する管理依存（例: tree-sitter プラグインの `TreeSitter.dll`）も同じ `lib/` に同居させる。**`.deps.json` は不要**——`PluginLoadContext`（ALC）が `.deps.json` で解決できない管理依存を **`lib/` 直下の同名 DLL として直接プローブ**するため、プラグイン配布物は管理 DLL だけでよい（フレームワーク assembly は `lib/` に無いので既定コンテキストへ委ねられる）。

> **tree-sitter ネイティブ**（constants／file-rules が使う `tree-sitter-*.dll`）は `lib/` ではなく**実行体隣の `runtimes/<rid>/native/`** に置く。TreeSitter.DotNet が grammar を「ベア名」で `NativeLibrary.Load` するため `.deps.json`／ALC のネイティブ解決を経由せず、OS 既定探索（実行体ディレクトリ・system・PATH）に無いと `DllNotFoundException` になる。host（`ai-harness-main`）は daemon／standalone 起動時に `runtimes/<現在の RID>/native/` の各ファイルをフルパスで事前ロードし、以降のベア名ロードを OS の既ロード解決に委ねる（PATH や探索パスは変更しない）。
>
### native 配布ポリシー

native の入手経路は使用者が置くもの（host と `lib/` のプラグイン DLL）に限られる。使用者に `runtimes/` を触らせない・winget を前提にしない・既定リリースを汚さない、を満たすため次で固定する。

1. **既定リリースに native を同梱してよいのは tree-sitter のみ。** 汎用（どの tree-sitter プラグインでも同一）の first-party 依存なので、host のリリース zip に `runtimes/<rid>/native/` として封入する。tree-sitter プラグインの配布物は `lib/` のマネージド DLL のみ。同梱 native の版はプラグインが参照する `TreeSitter.dll`（現状 `TreeSitter.DotNet 1.3.0`）と一致させる。

2. **それ以外の native は既定リリースに一切含めない**（host にも `lib/` にも置かない）。使用者は `runtimes/` を触らない（追加不可）。ただし**プログラム（host）が `runtimes/` を書き換えるのは可**。

3. **tree-sitter 以外で native が要るプラグインは、native を自分の管理 DLL に埋め込む（embedded resource）。** host が起動時に `runtimes/<rid>/native/` へ**自動展開**してから事前ロードする。使用者は従来どおり管理 DLL を `lib/` に置くだけで、`runtimes/` を触らない。native はプラグイン DLL に同梱されるため**完全オフライン**（ネットワーク・外部インストーラ不要）。native を `DllImport`／ベア名のどちらでロードしても、`runtimes/` 事前ロード（既ロード解決）で効く。

   - 自動展開は**グローバル単一の `runtimes/`・冪等（既に在れば何もしない）・起動時 1 回（型発見時）**。複数プロジェクト（マルチテナント）や複数プラグインが同じ native を要求しても、`runtimes/` には 1 つだけ・展開も 1 回だけ。初回展開の競合は `.tmp` → atomic rename で回避する。
   - この host 側の自動展開フックは、**最初の非 tree-sitter native プラグインが登場した時点で実装**する（tree-sitter は既定同梱なので現状は不要）。`PreloadNativeLibraries` による `runtimes/` 事前ロードの仕組みは共通で流用する。

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
