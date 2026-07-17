# 設定とログ

ハーネスの設定ファイル、ディレクトリ規約、ログの仕様。

## ディレクトリ規約

パスは 2 系統に分かれる。**グローバル**（実行体基準・全プロジェクト共有）と、**プロジェクト個別**（プロジェクトルート基準）。
プロジェクトルートは bridge が cwd から `.claude` を上方探索して決めた「`.claude` を含む階層」。

### グローバル（実行体 = `AppContext.BaseDirectory` 基準）

| パス | 用途 | 生成 |
|---|---|---|
| `<実行体>/config/` | 本体直下の設定（`plugins.yml`＝プラグインインストール定義／`daemon.yml`＝daemon の寿命） | 手動 |
| `<実行体>/lib/` | 共有プラグイン DLL の走査先（全プロジェクト共通） | 手動／`--update` |
| `<実行体>/repos/` | `--update` がプラグインを clone／build する作業領域 | 自動（`--update`） |
| `<実行体>/run/` | daemon 作業領域（`daemon.lock`） | 自動 |
| `<実行体>/logs/` | daemon ライフサイクルログ（型発見・起動・回収・停止） | 自動 |

### プロジェクト個別（`<ルート>/.claude/harness/` 基準）

| パス | 用途 | 生成 |
|---|---|---|
| `<ルート>/.claude/harness/config/` | 設定（`common.yml` ＋ 各プラグイン YAML） | 手動 |
| `<ルート>/.claude/harness/logs/` | hook 処理ログ（`<yyyy-MM-dd>.jsonl`） | 自動 |

## plugins.yml（プラグインインストール定義）

本体（実行体）直下の `config/plugins.yml` に置く**グローバル**設定。プロジェクト個別ではない。
`ai-harness-main --update` がこの定義に従い、各リポジトリを `<実行体>/repos/` へ clone／pull・build し、
成果の管理 DLL を `<実行体>/lib/` へ配置する。本体（`ai-harness-main` 自身）は更新対象外。

```yaml
# 拡張プラグインがビルド時に ProjectReference で参照する共有ライブラリ（baselib）。省略時は既定値。
baselib:
  path: https://github.com/HatoriIchigo/ai-harness-baselib
  branch: main

plugins:
  # path:   clone 元のリポジトリ URL
  # branch: 取得するブランチ（未指定は main）
  - path: https://github.com/HatoriIchigo/ai-harness-file-rules
    branch: main
```

| 項目 | 内容 |
|---|---|
| 前提 | `git`／`dotnet` が PATH に必要。無ければ `--update` は異常終了（非 0）で何もしない |
| baselib | プラグインより先に `repos/ai-harness-baselib` へ用意。各プラグインの csproj が `..\..\ai-harness-baselib\...` と兄弟ディレクトリを相対参照するため必須。用意に失敗したら以降を中止して異常終了 |
| 更新 | 既に `repos/<名前>` があれば `git fetch --depth 1` ＋ `reset --hard FETCH_HEAD` で最新化。無ければ shallow clone |
| build | リポジトリ内の csproj（名前一致を優先）を `dotnet build -c Release` |
| 配置 | build 出力の `*.dll`（`ai-harness-baselib.dll` は除外）を `lib/` へ上書きコピー |
| 反映 | 配置後、稼働中の daemon を自動 `--restart`（DLL 差し替え反映） |

> `baselib` は「本体（`ai-harness-main` 自身）」ではなく**プラグインのビルド依存**。`repos/` 配下に
> プラグインと**兄弟**として置くことで各プラグインの相対 `ProjectReference` が解決する。稼働中の本体
> バイナリは差し替えない。clone 先ディレクトリ名は相対参照に合わせ `ai-harness-baselib` に固定。

> `--update` で `lib/` に入れただけではプラグインは発火しない。各プロジェクトの `common.yml` の
> `tools` で当該 PluginName を `true` にして初めて有効化される（インストールと有効化は別）。

## daemon.yml（daemon の寿命）

本体（実行体）直下の `config/daemon.yml` に置く**グローバル**設定。単一の共有 daemon が全プロジェクトを
さばくため、プロジェクト個別ではない。

```yaml
# プロジェクトの状態をメモリに保つ時間（分）。既定 30。
evictAfterMinutes: 30

# 接続が全く無い状態がこれを超えたら、生存プロジェクトの有無を確認する（分）。既定 5。
idleShutdownMinutes: 5

# スイーパの走査間隔（秒）。既定 60。
sweepIntervalSeconds: 60
```

| キー | 既定 | 内容 |
|---|---|---|
| `evictAfterMinutes` | `30` | 無アクセスがこれを超えたプロジェクトをスイーパが回収する。次のアクセスで再構築されるため、短いほどメモリを解放しやすく、長いほど再構築（設定の再読込・`Init`）を避けられる |
| `idleShutdownMinutes` | `5` | 接続途絶時に生存プロジェクトの有無を確認する間隔。空なら daemon 終了、生存していれば待受継続 |
| `sweepIntervalSeconds` | `60` | 回収判定の粒度。実際の回収は最大この分だけ遅れる |

- **メモリ保持時間を決めるのは `evictAfterMinutes` の 1 つだけ。** 全プロジェクトが回収されてメモリが空になった時点で daemon 自体も終了するため、この値が「Claude Code 終了後に daemon が居座る時間」の上限になる。`idleShutdownMinutes` は保持時間の上限ではなく、確認の間隔にすぎない。
- 読むのは **daemon 起動時の 1 回のみ**。変更の反映には再起動が要る（`ai-harness-main --restart`）。プロジェクト個別設定のホットリロードとは別系統。
- ファイルが無い・壊れている・値が 1 未満の場合は、その項目の**既定値で継続**する（警告ログを出す）。何も強制しない実行時パラメータのため、`common.yml` と違いフェイルクローズしない。
- 起動時のログに実効値が出る（`daemon 起動 pipe=… 保持=30分 受付アイドル=5分 走査間隔=60秒`）。

## common.yml

プロジェクト個別の可変設定。`<ルート>/.claude/harness/config/common.yml` に置く。

```yaml
# 診断ログの出力閾値。これ未満のレベルは破棄。
# 指定可能: Trace / Debug / Info / Warning / Error
logLevel: Info

# プラグイン発火の同時実行数上限。0 または未指定で論理プロセッサ数。
maxParallel: 0

# 有効化するプラグイン。キーは各プラグインの PluginName、値は true/false。
tools:
  - ai-harness-deny: true
  - ai-harness-inject: false
```

| キー | 既定値 | 説明 |
|---|---|---|
| `logLevel` | `Info` | この閾値以上のレベルのみ出力（`Trace < Debug < Info < Warning < Error`） |
| `maxParallel` | 論理プロセッサ数 | プラグイン並列発火の同時数上限。`0`／未指定／不正値で既定 |
| `tools` | （空＝全 off） | プラグインごとの有効/無効。単一エントリマップのリスト。下記参照 |

- ファイルが**欠落**しているプロジェクトはハーネス対象外。hook は素通りする（deny しない）。
- ファイルが**在るのに壊れている**場合は**フェイルクローズ**＝そのプロジェクトの hook を全て deny する（何を強制すべきか判断できないため）。直せばホットリロードでブロックが解ける。
- 未知のキーは無視される（前方互換）。
- 値の解釈: `logLevel` は大文字小文字を区別しない。`maxParallel` は 0 以下を既定へフォールバック。

### tools（プラグインの有効化）

`lib/`（共有）に導入したプラグインを、このプロジェクトで **PluginName** 単位に on/off する。キーは各プラグインの `PluginName`（DLL 名でも AssemblyName でもなく、`PluginBase.PluginName` の値）。

- `true` のプラグインのみ有効化。起動時にログへ `<PluginName>: 起動しました` を記録する。
- `false` および**未記載**のプラグインは無効（一切発火しない）。`tools` セクション自体が無い／空なら**全プラグインが off**。
- `true` にしたプラグインを発火できる状態に持ち込めない場合は**フェイルクローズ**＝そのプロジェクトの hook を全て deny する。「有効化したガードが黙って効かない」状態を作らないため。該当するのは次のとき。
  - その PluginName が `lib/` に存在しない。
  - そのプラグインの設定ファイル（`ConfigName`）がこのプロジェクトの `config/` に無い。
  - `Init` や発火条件（`Tools`/`Events`）の検証に失敗した。

  いずれも `ai-harness-main --validate` で hook を待たずに検出できる。

```yaml
tools:
  - ai-harness-deny: true      # 有効化（起動ログを出す）
  - ai-harness-inject: false   # 無効（除外）
  # 記載しないプラグインも無効
```

### CLI での有効化・無効化

`tools` は手で書くほか、CLI からも切り替えられる。書き換えはコメントとキー順を保ったまま該当行だけを差し替える。設定 YAML はホットリロード対象なので、**daemon の再起動は要らない**。

```bash
# cwd のプロジェクト（.claude を上方探索して決定）
ai-harness-main --plugin --enable ai-harness-deny
ai-harness-main --plugin --disable ai-harness-deny

# プロジェクトを明示、カンマ区切りで複数
ai-harness-main --plugin <プロジェクト> --enable ai-harness-deny,ai-harness-constants
```

- `common.yml` が無ければ既定テンプレートから**新規作成**する。
- 有効化がフェイルクローズを招く場合（`lib/` に無い、設定 YAML が置かれていない等）は、**書き込まずに拒否**して理由を出す。有効化したつもりで hook が全停止する事態を防ぐ。
- 無効化は常に許す（フェイルクローズからの復旧経路になるため）。
- 現在の状態は `ai-harness-main --plugin <プロジェクト>` で確認する。

## プラグインの設定ファイル

各プラグインは `ConfigName`（必須）で自身の YAML ファイル名を宣言する。プロジェクトの設定ディレクトリ
`<ルート>/.claude/harness/config/<ConfigName>` を読む。

- ファイルが**無い／読めない**と、そのプラグインはそのプロジェクトで**無効化**される（フェイルクローズ）。
- 設定値が不要でも、空ファイルを置けば空マッピングとして有効化される。
- プラグインからは `Config`（`IReadOnlyDictionary<string, object>`）で参照する。

例（`DenyMarker` プラグイン）:

```yaml
# .claude/harness/config/denymarker.yml
marker: "DENYME"
```

## 設定の反映（ホットリロード）

`common.yml`・各プラグイン YAML は**ホットリロード対象**。保存すると daemon が `FileSystemWatcher` で検知し、デバウンス後に当該プロジェクトの有効プラグイン集合を再構築（設定再ロード・`Init` 再実行）する。daemon の再起動は不要。

`lib/` のプラグイン DLL を差し替えた場合と、`daemon.yml` を変えた場合のみ daemon の再起動が必要
（前者は型を読み直すため、後者は起動時に 1 回しか読まないため）。

```sh
ai-harness-main --restart   # lib の DLL 差し替え・daemon.yml の変更を反映
```

## ログ

ログは 2 系統に出る。

- **daemon ライフサイクル**（型発見・起動・プロジェクト回収・停止）: `<実行体>/logs/<yyyy-MM-dd>.jsonl`。
- **hook 処理**（`claude`＝ハーネス由来 ／ 各プラグイン）: `<ルート>/.claude/harness/logs/<yyyy-MM-dd>.jsonl`。

いずれも 1 行 1 JSON。

```json
{"timestamp":"...","level":"Info","source":"my-plugin","message":"..."}
```

| フィールド | 内容 |
|---|---|
| `timestamp` | 出力時刻 |
| `level` | `Trace`／`Debug`／`Info`／`Warning`／`Error` |
| `source` | `claude`（ハーネス由来）またはプラグインの `PluginName` |
| `message` | 本文 |

- `logLevel` 閾値未満のレベルは破棄される（プロジェクトログは当該プロジェクトの `common.yml` に従う）。
- daemon 時は stderr に出さず**ファイルのみ**。`--standalone` 時は stderr にも出る。
- `source` はプラグインが設定不要。main が `PluginName` を打刻する。
