# 設定とログ

ハーネスの設定ファイル、ディレクトリ規約、ログの仕様。

## ディレクトリ規約

ディレクトリは実行体（`AppContext.BaseDirectory`）からの相対で**固定**。環境変数では変更しない。

| パス | 用途 | 生成 |
|---|---|---|
| `<実行体>/lib/` | プラグイン DLL の走査先 | 手動 |
| `<実行体>/config/` | 設定ファイル（`main.yml` ＋ 各プラグイン YAML） | 手動 |
| `<実行体>/logs/` | 統合ログ（`<yyyy-MM-dd>.jsonl`） | 自動 |
| `<実行体>/run/` | daemon 作業領域（`daemon.lock`） | 自動 |

## main.yml

ハーネス本体の可変設定。`config/main.yml` に置く。

```yaml
# 診断ログの出力閾値。これ未満のレベルは破棄。
# 指定可能: Trace / Debug / Info / Warning / Error
logLevel: Info

# プラグイン発火の同時実行数上限。0 または未指定で論理プロセッサ数。
maxParallel: 0
```

| キー | 既定値 | 説明 |
|---|---|---|
| `logLevel` | `Info` | この閾値以上のレベルのみ出力（`Trace < Debug < Info < Warning < Error`） |
| `maxParallel` | 論理プロセッサ数 | プラグイン並列発火の同時数上限。`0`／未指定／不正値で既定 |

- ファイルが**欠落・破損**しても既定値で起動する（警告ログを 1 行出す）。
- 未知のキーは無視される（前方互換）。
- 値の解釈: `logLevel` は大文字小文字を区別しない。`maxParallel` は 0 以下を既定へフォールバック。

## プラグインの設定ファイル

各プラグインは `ConfigName`（必須）で自身の YAML ファイル名を宣言する。`config/<ConfigName>` を読む。

- ファイルが**無い／読めない**と、そのプラグインは起動時に**無効化**される（フェイルクローズ）。
- 設定値が不要でも、空ファイルを置けば空マッピングとして有効化される。
- プラグインからは `Config`（`IReadOnlyDictionary<string, object>`）で参照する。

例（`DenyMarker` プラグイン）:

```yaml
# config/denymarker.yml
marker: "DENYME"
```

## 設定の反映

`main.yml` もプラグイン DLL（`lib/`）も daemon 起動時に**一度だけ**読む。変更後は再起動が必要。

```sh
ai-harness-main --restart
```

## ログ

全ログ（`claude` ＝ハーネス由来 ／ 各プラグイン）を単一ファイル `logs/<yyyy-MM-dd>.jsonl` に集約する。1 行 1 JSON。

```json
{"timestamp":"...","level":"Info","source":"my-plugin","message":"..."}
```

| フィールド | 内容 |
|---|---|
| `timestamp` | 出力時刻 |
| `level` | `Trace`／`Debug`／`Info`／`Warning`／`Error` |
| `source` | `claude`（ハーネス由来）またはプラグインの `PluginName` |
| `message` | 本文 |

- `logLevel` 閾値未満のレベルは破棄される。
- daemon 時は stderr に出さず**ファイルのみ**。standalone 時は stderr にも出る。
- `source` はプラグインが設定不要。main が `PluginName` を打刻する。
