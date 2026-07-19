# クイックスタート

まっさらな環境から、**Claude Code の危険な操作が実際に deny されるところまで**を一通り通す手順。
所要 10 分程度。ここでは最小構成として `ai-harness-deny`（`PreToolUse` で禁止コマンド・禁止ファイルを差し戻す）
1 つだけを有効化する。他プラグインの追加は最後の[プラグインを増やす](#プラグインを増やす)で扱う。

全体像は [architecture.md](architecture.md)、設定の詳細は [configuration.md](configuration.md)、
発行・配置の詳細は [build-and-deploy.md](build-and-deploy.md) を参照。

## 0. できあがる状態

```
Claude Code ──hook──▶ ai-harness-main (bridge) ──pipe──▶ ai-harness-main (daemon)
                                                              └──▶ lib/ai-harness-deny.dll
```

- Claude Code がツールを使う直前（`PreToolUse`）にハーネスが呼ばれる。
- `ai-harness-deny` がルールにマッチしたツール実行を **deny**（exit 2）し、Claude Code に差し戻す。
- 設定は**プロジェクトごと**（`.claude/harness/`）、実行体とプラグインは**全プロジェクト共有**。

## 1. 前提

| 必要なもの | 確認 |
|---|---|
| .NET SDK 10.0 以降 | `dotnet --version` が `10.` 以上 |
| Claude Code | 対象プロジェクトで動作している |
| git（任意） | `--update` でプラグインを自動取得する場合のみ |

```powershell
# Windows。未導入なら
winget install Microsoft.DotNet.SDK.10
```

## 2. 本体を発行する

配置先に .NET ランタイムを要求しないよう、**self-contained 単一ファイル**で発行する。
`<インストール先>` は任意のディレクトリ（例: `C:\Users\you\ai-harness\bin` / `~/.local/share/ai-harness`）。

```powershell
# Windows (PowerShell)
git clone https://github.com/HatoriIchigo/ai-harness-main
git clone https://github.com/HatoriIchigo/ai-harness-baselib   # 兄弟ディレクトリに置く（ProjectReference が相対参照）

dotnet publish ai-harness-main\ai-harness-main\ai-harness-main.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o <インストール先>
```

```sh
# Linux
git clone https://github.com/HatoriIchigo/ai-harness-main
git clone https://github.com/HatoriIchigo/ai-harness-baselib

dotnet publish ai-harness-main/ai-harness-main/ai-harness-main.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o <インストール先>
```

RID は配置先 OS に合わせる（`win-x64` / `win-arm64` / `linux-x64` / `linux-arm64` / `osx-arm64` / `osx-x64`）。

## 3. PATH を通す

hook から `ai-harness-main` という**名前だけ**で起動できる必要がある（Claude Code の `settings.json` に
絶対パスを書かせない設計のため）。

```powershell
# Windows: ユーザー PATH に追加（新しいシェルから有効）
[Environment]::SetEnvironmentVariable(
  "Path",
  [Environment]::GetEnvironmentVariable("Path", "User") + ";<インストール先>",
  "User")
```

```sh
# Linux: symlink
sudo ln -sf <インストール先>/ai-harness-main /usr/local/bin/ai-harness-main
```

確認:

```sh
ai-harness-main --version
```

## 4. プラグインを配置する

プラグイン DLL は**インストール先の `lib/`** に置く（全プロジェクト共有）。手動でも自動でもよい。

### 自動（`--update`。git・dotnet が PATH に必要）

```sh
mkdir -p <インストール先>/config
cp ai-harness-main/ai-harness-main/config/plugins.yml <インストール先>/config/
```

`<インストール先>/config/plugins.yml` に欲しいプラグインを列挙して:

```yaml
plugins:
  - path: https://github.com/HatoriIchigo/ai-harness-deny
    branch: main
```

```sh
ai-harness-main --update
```

`repos/` へ clone → build → `lib/` へ配置し、稼働中の daemon を自動再起動する。

### 手動

プラグインを自分でビルドし、出力の `ai-harness-*.dll` を `lib/` にコピーする。

```sh
git clone https://github.com/HatoriIchigo/ai-harness-deny
dotnet build ai-harness-deny/ai-harness-deny/ai-harness-deny.csproj -c Release
mkdir -p <インストール先>/lib
cp ai-harness-deny/ai-harness-deny/bin/Release/net10.0/ai-harness-deny.dll <インストール先>/lib/
```

> `lib/` に置くのは**プラグインの管理 DLL だけ**。`ai-harness-baselib.dll` は host が共有ロードするので置かない。

置けたか確認:

```sh
ai-harness-main --plugin      # lib/ のインストール済みプラグイン一覧
```

> `lib/` に入れただけでは**発火しない**。次の手順でプロジェクトごとに有効化する。

## 5. プロジェクトを配線する

対象プロジェクトのルート（`.claude` がある階層、または cwd）で `ai-harness-main --init` を実行する。

```sh
ai-harness-main --init     # cwd のプロジェクト（.claude が無ければ新規に配線する）
```

やることは 2 つ。

1. `.claude/settings.json` に `ai-harness-main` を呼ぶ `PreToolUse`／`PostToolUse` hook を追記する
   （既存の設定・他ツールの hook は保持。既に配線済みなら変更しない）。
2. `lib/` にインストール済みのプラグインを一覧にし、有効化するものを対話的に選ばせる
   （↑/↓ か j/k で移動、space でチェック切替、Enter で確定）。選んだプラグインは
   `.claude/harness/config/common.yml` の `tools` へ書き込まれ、無ければデフォルト設定 YAML も
   同時に配置される（`ai-harness-main --plugin --enable` と同じ検証・書き込み経路）。

選ぶプラグインが決まっているならプロンプトを飛ばせる。

```sh
ai-harness-main --init --enable ai-harness-deny,ai-harness-git-commit
```

`--init` はプロジェクト無指定なら cwd から解決する（`.claude` が見つからなければ cwd 自体を配線先にする）。
プラグインを追加・変更したいだけなら `ai-harness-main --plugin --enable|--disable <名,…>` で
`common.yml` だけを直接書き換えればよい（settings.json には触れない）。

`.claude/settings.json` に書かれる hook は次の形（`--init` が書くものと同一）。

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "ai-harness-main" } ] }
    ],
    "PostToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "ai-harness-main" } ] }
    ]
  }
}
```

`common.yml` の `tools` は `PluginName`（DLL 名ではない）をキーにした真偽値のリスト。

```yaml
logLevel: Info
maxParallel: 0

tools:
  - ai-harness-deny: true
```

書かなかったプラグインは全て無効。


## 6. 動くか確かめる

まず環境とプロジェクト設定を、Claude Code を待たずに検証する。

```sh
ai-harness-main --doctor                  # この配置でハーネスが機能するか（lib・native・daemon）
ai-harness-main --validate                # cwd のプロジェクト設定で hook が通るか。0=成功 / 1=失敗
ai-harness-main --plugin <プロジェクト>   # そのプロジェクトでの有効/無効
```

`--validate` が 0 を返せば配線は完了。あとは Claude Code を（設定を読み直させるため）起動し直し、
ルールに引っかかる操作を頼んでみる。

```
> .env を読んで
```

deny されれば成功で、Claude Code には理由が返り、ハーネスのログに監査レコードが残る。

```sh
ai-harness-main --logs <プロジェクト> --deny          # deny だけを新しい順に
ai-harness-main --logs <プロジェクト> --n 20          # 直近 20 件
ai-harness-main --project                             # daemon がメモリに載せているプロジェクト
```

deny のレベルは由来で分かれる。**ルールが効いた deny は `warn`**、**フェイルクローズ（検証できずブロック）は `error`**。
つまり `--deny --filter error` は「ハーネスの不調でブロックした件」だけを拾う。

## 7. うまく動かないとき

| 症状 | 原因と対処 |
|---|---|
| hook が呼ばれた形跡がない | `ai-harness-main --version` が通るか（PATH）。Claude Code を再起動したか |
| 何をしても deny される | フェイルクローズ。`--validate` を実行する。有効化したプラグインの YAML 欠落・`common.yml` の破損・`lib/` に DLL が無い、のいずれか |
| プラグインが発火しない | `--plugin <プロジェクト>` で有効か確認。`common.yml` の `tools` のキーは `PluginName`（DLL 名ではない） |
| DLL を差し替えたのに反映されない | `ai-harness-main --restart`（設定 YAML はホットリロードされるが、DLL は daemon 再起動が要る） |
| tree-sitter 系が全 deny する | native grammar のロード失敗。`--doctor` で切り分ける（`runtimes/<rid>/native/` が要る） |

daemon は放っておいてよい。無アクセス 30 分で全プロジェクトを回収し、空になれば自分で終了する
（時間は `<インストール先>/config/daemon.yml` の `evictAfterMinutes` で変えられる）。

## プラグインを増やす

`lib/` に DLL を足し（手順 4）、そのプラグインの YAML を置いてから有効化する（手順 5）。

```sh
ai-harness-main --plugin --enable <PluginName>
```

設定 YAML はホットリロードされるので daemon の再起動は不要。DLL を足したときだけ `--restart`。
YAML を置く前に有効化しようとすると、フェイルクローズ（全 deny）を避けるため `--enable` が拒否する。

| プラグイン | 何を止めるか | hook |
|---|---|---|
| [`ai-harness-deny`](https://github.com/HatoriIchigo/ai-harness-deny) | 禁止コマンド・禁止ファイルへのツール実行 | `PreToolUse` |
| [`ai-harness-git-commit`](https://github.com/HatoriIchigo/ai-harness-git-commit) | コミットメッセージ規約違反（tag・文字数・禁止語） | `PreToolUse` |
| [`ai-harness-directory-checker`](https://github.com/HatoriIchigo/ai-harness-directory-checker) | 許可 glob から外れた場所へのファイル配置 | `PostToolUse` |
| [`ai-harness-constants`](https://github.com/HatoriIchigo/ai-harness-constants) | 定数ファイル以外へのハードコード値 | `PostToolUse` |
| [`ai-harness-file-rules`](https://github.com/HatoriIchigo/ai-harness-file-rules) | 行数・1 クラス 1 ファイル・メソッド数などの構造違反 | `PostToolUse` |
| [`ai-harness-import-rules`](https://github.com/HatoriIchigo/ai-harness-import-rules) | import 依存規約違反（禁止 import・強制 import） | `PostToolUse` |
| [`ai-harness-banned-words`](https://github.com/HatoriIchigo/ai-harness-banned-words) | 禁止ワード（生テキスト／関数名・クラス名・変数名） | `PostToolUse` |

自作する場合は [plugin-development.md](plugin-development.md) を参照。

## 次に読むもの

| ドキュメント | 内容 |
|---|---|
| [cli.md](cli.md) | コマンドラインの全モード・オプション・終了コードの体系 |
| [configuration.md](configuration.md) | `common.yml`・プラグイン設定・ディレクトリ規約・ログの全仕様 |
| [architecture.md](architecture.md) | bridge／daemon・IPC・マルチテナント・ライフサイクル |
| [build-and-deploy.md](build-and-deploy.md) | 発行・配置レイアウト・native 配布ポリシー・daemon 制御 |
| [plugin-development.md](plugin-development.md) | プラグインの作り方（`PluginBase`・発火条件・配置） |
