# ai-harness-main

## 概要

LLMエージェントの暴走・逸脱を、skillsやrulesなどのプロンプトにたよらず、機械的な制約で制御していくプログラムです。
Claude Code の hook を受け、拡張プラグイン（deny・ディレクトリ検証・コミット規約など）を機械的に発火させ、規約に外れたアクションを**確実に差し戻します**。
「勝手にディレクトリを作る」「許可外の場所にファイルを置く」「禁止コマンドを打つ」といった逸脱を、起きた後ではなく**起きる境界で機械的に**止めるを目的としています。

## 背景

LLM エージェントは確率的に振る舞います。
「このディレクトリ以外に書くな」「このコマンドは打つな」をいくらプロンプトやドキュメント、skillsなどで指示しても、それは*お願い*であり守られる保証がありません。
そのため、モデルの善意ではなく仕組みでアクションを律する仕組みが必須でした。
`ai-harness-main` はその実行基盤となるプログラムです。

## アーキテクチャ概要

単一の実行体 `ai-harness-main` が、hook ごとの**受け口（bridge）**と常駐**サーバー（daemon）**を兼ねる。
hook が叩かれると bridge モードで起動し、cwd からプロジェクトルート（`.claude` を含む階層）を解決して
名前付きパイプ越しに daemon へ中継する。daemon は `lib/`（全プロジェクト共有）の拡張プラグイン
（`PluginBase` 派生 DLL）を並列発火し、結果を **deny 先勝ち**で集約して返す。

単一の daemon が**複数プロジェクトをさばく**（マルチテナント）。設定・ログはプロジェクトごとの
`.claude/harness/` 配下に分離し、各種 YAML は**ホットリロード**で無停止反映する。

## 特徴

- **機械的強制（決定論的ガードレール）**:
  規約を hook で強制し、外れたアクションを deny で差し戻す。プロンプト依存の"お願い"にしない。
- **単一バイナリ**:
  bridge と daemon を 1 つの実行体が兼ねる。配置物は 1 つ。
- **共有 daemon でウォーム保持**:
  プラグインの型発見は起動時 1 回。全プロジェクトで共有し、hook ごとの遅延を抑える。
- **ホットリロード**:
  プロジェクトの `common.yml`・各プラグイン YAML の変更を `FileSystemWatcher` で検知し無停止反映。
- **アイドル回収＋自動停止**:
  無アクセス 5 分でプロジェクト状態を破棄。全プロジェクトが回収されメモリが空になれば daemon 自体が終了（Claude 終了後の居座り防止）。
- **deny 先勝ち集約**:
  1 つでも非 0 を返せば全体 deny。理由を Claude Code へ返す。
- **フェイルクローズ（検証できないなら通さない）**:
  プラグインのクラッシュ・内部エラー・`common.yml` 不正など hook を検証できなかった場合は deny（ブロック）。
  `tools` で**有効化したプラグインが起動できなかった**場合（`lib` に無い・設定 YAML が無い/壊れている・`Init` が失敗）も、そのガードが効かないため deny する。
  例外は **bridge が daemon にまったく接続できない**（基盤ごと停止）ときのみで、全ツールのロックアウトを避けるため許可で継続する。
- **クロスプラットフォーム**:
  Windows／Linux を単一コードで対応（IPC の OS 差は .NET が吸収）。

## インストール

> **はじめて導入するなら [docs/quickstart.md](docs/quickstart.md)。**
> 発行 → PATH → プラグイン配置 → プロジェクト配線 → deny が実際に効くまでを、Windows／Linux の
> 具体コマンドで一通り通す最短手順（10 分程度）。以下は要点の抜粋。

### Windows

```powershell
# 1. dotnet のインストール（なければ）
winget install Microsoft.DotNet.SDK.10

# 2. self-contained 単一ファイルで発行
dotnet publish ai-harness-main\ai-harness-main\ai-harness-main.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o <インストール先>

# 3. PATH に <インストール先> を追加（hook は名前だけで ai-harness-main を起動する）
```

### Linux

```sh
# 1. self-contained 単一ファイルで発行（Linux の例）
dotnet publish ai-harness-main/ai-harness-main/ai-harness-main.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <インストール先>

# 2. PATH 解決できるようにする（Linux: symlink、Windows: PATH へ追加）
ln -sf <インストール先>/ai-harness-main /usr/local/bin/ai-harness-main

# 3. 共有プラグインを配置（インストール先の lib/）
mkdir -p <インストール先>/lib
cp <plugin>.dll <インストール先>/lib/

# 4. プロジェクト側に設定を置く
mkdir -p <プロジェクト>/.claude/harness/config
cp ai-harness-main/ai-harness-main/config/common.yml <プロジェクト>/.claude/harness/config/
cp <plugin>.yml <プロジェクト>/.claude/harness/config/
```

## 各プロジェクト側のClaude Codeの設定

Claude Code の `settings.json` では hook コマンドに **`ai-harness-main`** を指定する（PATH 解決・環境変数不要）。

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "ai-harness-main" } ] }
    ]
  }
}
```

詳細は [docs/build-and-deploy.md](docs/build-and-deploy.md) を参照。

## 実行モード

単一バイナリで、**第 1 引数のモードで役割が変わる**。bridge になるのは**引数なし**のときだけで、
未知の引数は使い方を出して 1 で終わる（typo を hook として扱わない）。

| 起動 | 役割 |
|---|---|
| （引数なし） | bridge。hook ごとに Claude Code が叩く受け口。stdin を daemon へ中継。未起動なら daemon を起動 |
| `--daemon` / `--ensure` / `--restart` / `--stop` | daemon の制御。`--restart` は `lib` のプラグイン DLL 差し替え反映用 |
| `--standalone` | daemon を介さず stdin を 1 件処理して終了（テスト・フォールバック） |
| `--update` | `config/plugins.yml` に従いプラグインを `lib/` へ配置し、本体自身も置換（自己更新） |
| `--validate [プロジェクト]` | 設定で hook が通る状態か検証。0=成功 / 1=失敗 |
| `--doctor` | この配置でハーネスが機能するか診断（`lib`・native・daemon・`git`/`dotnet`） |
| `--project` / `--logs` / `--plugin` | 読み取り専用の情報表示（展開中プロジェクト／ログ／プラグイン） |
| `--fire [プラグイン名]` | 有効プラグインの能動スキャン。hook とは独立でゲートではない。0=問題なし / 2=検出 / 1=実行不能 |
| `--version` / `--help` | 版の表示／使い方 |

各モードのオプション・終了コード・使い分けは **[docs/cli.md](docs/cli.md)** を参照。

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [docs/quickstart.md](docs/quickstart.md) | **最短導入**（発行・PATH・プラグイン配置・プロジェクト配線・動作確認・つまずき集） |
| [docs/cli.md](docs/cli.md) | **コマンドライン**（全モード・オプション・終了コードの体系） |
| [docs/architecture.md](docs/architecture.md) | 全体構成・コンポーネント・処理フロー・IPC・マルチテナント・ライフサイクル |
| [docs/plugin-development.md](docs/plugin-development.md) | 新規プラグイン作成の手順（csproj・実装・発火条件・配置） |
| [docs/build-and-deploy.md](docs/build-and-deploy.md) | Windows／Linux のビルド・発行・配置・daemon 制御 |
| [docs/configuration.md](docs/configuration.md) | `common.yml`・プラグイン設定・ディレクトリ規約・ログ |
| [docs/self-update.md](docs/self-update.md) | `--update` の内部設計（プラグイン更新・本体自己更新・ロールバック） |

## プロジェクト構成

```
ai-harness-main/
├── README.md
├── docs/                       ドキュメント
└── ai-harness-main/
    ├── ai-harness-main.csproj
    ├── Program.cs              エントリ（モード分岐・standalone 処理）
    ├── Bridge/
    │   ├── Bridge.cs           bridge モード（hook 受け口・daemon へ中継）
    │   └── ProjectLocator.cs   cwd から .claude を上方探索してルート解決
    ├── Plugins/
    │   ├── PluginLoader.cs      DLL 走査・型発見
    │   ├── PluginRegistry.cs    共有型レジストリ（起動時 1 回・全プロジェクト共有）
    │   ├── ProjectContext.cs    プロジェクト別の検証・Init・発火・ホットリロード・回収
    │   ├── PluginHost.cs        リクエスト毎の発火・deny 集約
    │   └── PluginLoadContext.cs プラグイン DLL 用 ALC（baselib 共有）
    ├── Ipc/
    │   ├── Daemon.cs            常駐サーバ・マルチテナント・回収・ensure／stop／restart
    │   ├── RequestEnvelope.cs   bridge→daemon の封筒（projectRoot ＋ hook JSON）
    │   ├── HarnessPipe.cs       パイプ名生成
    │   ├── HookOutput.cs        hook 出力 JSON 組み立て
    │   ├── HookResponse.cs      daemon→bridge の応答
    │   └── Framing.cs           長さ前置フレーミング
    ├── Config/
    │   ├── InstallPaths.cs      実行体基準のグローバルパス（config／lib／repos／run／グローバル log）
    │   └── ProjectConfig.cs     プロジェクト個別設定（common.yml ロード）
    ├── Install/
    │   ├── PluginsConfig.cs     plugins.yml ロード（self／baselib／plugins のインストール定義）
    │   ├── PluginInstaller.cs   --update 実体（プラグインの clone／build／lib 配置、本体自己更新への橋渡し）
    │   └── SelfUpdater.cs       本体自己更新（tmp へ publish → --apply-update で実行体を置換・検証・ロールバック）
    ├── Logging/
    │   └── Logger.cs            レベルフィルタ＋ログ集約（出力先は引数）
    └── config/
        ├── common.yml           プロジェクト設定の既定値（配置元サンプル）
        └── plugins.yml          プラグインインストール定義（本体直下 config/ へ配置するサンプル）
```

## 関連プロジェクト

| プロジェクト | 役割 | 種別 |
|---|---|---|
| [`ai-harness-baselib`](https://github.com/HatoriIchigo/ai-harness-baselib) | プラグイン契約（`PluginBase`／`HookData`／`PluginResult`／`LogEntry` 等）を定義する共有ライブラリ | ライブラリ |
| [`ai-harness-deny`](https://github.com/HatoriIchigo/ai-harness-deny) | `PreToolUse` で `rules`／`bash`／`files` の 3 系統ルールにマッチしたツール実行を deny | プラグイン |
| [`ai-harness-git-commit`](https://github.com/HatoriIchigo/ai-harness-git-commit) | `git commit` のメッセージ規約（tag・文字数・禁止語）を強制し、違反時は作り直させる | プラグイン |
| [`ai-harness-directory-checker`](https://github.com/HatoriIchigo/ai-harness-directory-checker) | `PostToolUse` で書き込んだ `file_path` を許可 glob と照合し、外れた配置を deny して差し戻す | プラグイン |
| [`ai-harness-constants`](https://github.com/HatoriIchigo/ai-harness-constants) | `PostToolUse` で書き込んだソースを tree-sitter で AST 解析し、許可した定数ファイル以外のハードコード値を deny | プラグイン |
| [`ai-harness-file-rules`](https://github.com/HatoriIchigo/ai-harness-file-rules) | `PostToolUse` で書き込んだソースを tree-sitter で AST 解析し、ファイル単位のコード構造ルール（行数・1クラス1ファイル・メソッド数/行数）を強制 | プラグイン |
