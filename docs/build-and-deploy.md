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
├── config/                     本体設定（plugins.yml＝プラグインのインストール定義）
├── resources/                  プロジェクトへ配る既定テンプレート（common.yml・phase.yml）
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
cp <plugin>.yml <プロジェクト>/.claude/harness/config/

# common.yml は --plugin --enable が既定テンプレート（<インストール先>/resources/common.yml）から
# 自動生成する。手で置くなら同じテンプレートをコピーする。
ai-harness-main --plugin <プロジェクト> --enable <PluginName>
```

> `lib/` には**プラグインの管理 DLL のみ**を置く。`ai-harness-baselib.dll` は host が共有ロードするため置かない（プラグインの csproj 側で `<Private>false</Private>` と `CopyLocalLockFileAssemblies=false` を指定して出力から除外する）。loader も `ai-harness-baselib` という名前の DLL はスキップする。プラグインが参照する管理依存（例: tree-sitter プラグインの `TreeSitter.dll`）も同じ `lib/` に同居させる。**`.deps.json` は不要**——`PluginLoadContext`（ALC）が `.deps.json` で解決できない管理依存を **`lib/` 直下の同名 DLL として直接プローブ**するため、プラグイン配布物は管理 DLL だけでよい（フレームワーク assembly は `lib/` に無いので既定コンテキストへ委ねられる）。

> **tree-sitter ネイティブ**（constants／file-rules／sandbox が使う `libtree-sitter` と `tree-sitter-*` grammar）は `lib/` ではなく**実行体隣の `runtimes/<rid>/native/`** に置く。host（`ai-harness-main`）が**2 つの経路**で解決する。どちらか一方では足りない。
>
> 1. **grammar（ベア名の `NativeLibrary.Load`）** … TreeSitter.DotNet は grammar を「ベア名」で `NativeLibrary.Load` する。これは `.deps.json` も ALC のネイティブ解決も経由せず、OS 既定探索（実行体ディレクトリ・system・PATH）だけが効く。host は daemon／standalone 起動時に `runtimes/<現在の RID>/native/` の各ファイルを**フルパスで事前ロード**し（`PreloadNativeLibraries`）、以降のベア名ロードを OS の既ロード解決に委ねる（PATH や探索パスは変更しない）。
> 2. **コア（`DllImport("tree-sitter")`）** … こちらは ALC の `LoadUnmanagedDll` を通る。`lib/` へ手動配置した `TreeSitter.dll` は `.deps.json` を持たないため `AssemblyDependencyResolver` が解決できない。そこで `PluginLoadContext` が `runtimes/<rid>/native/` を**直接プローブ**する（管理依存を `lib/` 直下から直接プローブするのと同じ発想）。
>
> **事前ロードだけでは 2 を解決できない。** dlopen が既ロードとして再利用する鍵は **SONAME** であり、ファイル名と一致するとは限らない（実測: `libtree-sitter.so` の SONAME は `libtree-sitter.so.0.26`）。フルパスで事前ロードしても `DllImport("tree-sitter")` からの `libtree-sitter.so` 探索は既ロードに当たらず、OS 既定探索（実行体ディレクトリを**含まない**）に落ちて `DllNotFoundException` になる。
>
### native 配布ポリシー

native の入手経路は使用者が置くもの（host と `lib/` のプラグイン DLL）に限られる。使用者に `runtimes/` を触らせない・winget を前提にしない・既定リリースを汚さない、を満たすため次で固定する。

1. **既定リリースに native を同梱してよいのは tree-sitter のみ。** 汎用（どの tree-sitter プラグインでも同一）の first-party 依存なので、host のリリース zip に `runtimes/<rid>/native/` として封入する。tree-sitter プラグインの配布物は `lib/` のマネージド DLL のみ。同梱 native の版はプラグインが参照する `TreeSitter.dll`（現状 `TreeSitter.DotNet 1.3.0`）と一致させる。

2. **それ以外の native は既定リリースに一切含めない**（host にも `lib/` にも置かない）。使用者は `runtimes/` を触らない（追加不可）。ただし**プログラム（host）が `runtimes/` を書き換えるのは可**。

3. **tree-sitter 以外で native が要るプラグインは、native を自分の管理 DLL に埋め込む（embedded resource）。** host が起動時に `runtimes/<rid>/native/` へ**自動展開**してから事前ロードする。使用者は従来どおり管理 DLL を `lib/` に置くだけで、`runtimes/` を触らない。native はプラグイン DLL に同梱されるため**完全オフライン**（ネットワーク・外部インストーラ不要）。native を `DllImport`／ベア名のどちらでロードしても、`runtimes/` の事前ロードと `PluginLoadContext` の直接プローブ（上記 1・2）で効く。

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
| `ai-harness-main --update` | `config/plugins.yml` の拡張プラグインを `repos/` へ clone／build し `lib/` へ配置（本体は更新しない）。配置後に稼働中 daemon を自動再起動 |

## プラグインの一括インストール／更新（`--update`）

拡張プラグインの取得・ビルド・配置を自動化する。本体（`ai-harness-main` 自身）は更新しない。

```sh
# 1. 本体直下の config/plugins.yml を用意（インストール定義）
mkdir -p <インストール先>/config
cp ai-harness-main/ai-harness-main/config/plugins.yml <インストール先>/config/
#    → 必要なプラグインの path / branch を編集

# 2. 一括インストール／更新（git・dotnet が PATH に必要）
ai-harness-main --update
```

- `<インストール先>/repos/` に各リポジトリを shallow clone／pull し、`dotnet build -c Release` の成果 DLL（`ai-harness-baselib.dll` は除外）を `lib/` へ配置する。
- プラグインより先に **baselib を `repos/ai-harness-baselib` へ用意**する。各プラグインの csproj が `..\..\ai-harness-baselib\...` と兄弟ディレクトリを相対参照するため（baselib はプラグインのビルド依存であり、本体の更新ではない）。取得元は `plugins.yml` の `baselib`（省略時は既定リポジトリの `main`）。
- `git`／`dotnet` が PATH に無ければ何もせず異常終了（非 0）。
- 配置後、稼働中の daemon を自動 `--restart` して差し替えを反映する。
- `lib/` へ入れただけでは発火しない。各プロジェクトの `common.yml` の `tools` で有効化する。

- 設定 YAML（`common.yml`・各プラグイン YAML）はホットリロードされるため、`--restart` は不要。DLL を差し替えたときだけ `--restart`。
- 何も操作しなくても、全プロジェクトが回収されメモリが空になるか、接続が 5 分途絶えれば daemon は自動終了する。

## 既知の制約

- **多重起動抑止が best-effort**: Unix では `FileShare.None` ロックが cross-process 強制されないため、稀に daemon が多重起動し得る。`--ensure`／bridge のパイプ疎通チェックで大半は回避。
- **ゾンビプロセス**: detached daemon の終了後、PID1 が reap しない環境（非 systemd の最小コンテナ等）で defunct 化し得る。無害。実機 init では回収される。
