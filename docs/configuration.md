# 設定とログ

ハーネスの設定ファイル、ディレクトリ規約、ログの仕様。

## ディレクトリ規約

パスは 2 系統に分かれる。**グローバル**（実行体基準・全プロジェクト共有）と、**プロジェクト個別**（プロジェクトルート基準）。
プロジェクトルートは bridge が cwd から `.claude` を上方探索して決めた「`.claude` を含む階層」。

### グローバル（実行体 = `AppContext.BaseDirectory` 基準）

| パス | 用途 | 生成 |
|---|---|---|
| `<実行体>/lib/` | 共有プラグイン DLL の走査先（全プロジェクト共通） | 手動 |
| `<実行体>/run/` | daemon 作業領域（`daemon.lock`） | 自動 |
| `<実行体>/logs/` | daemon ライフサイクルログ（型発見・起動・回収・停止） | 自動 |

### プロジェクト個別（`<ルート>/.claude/harness/` 基準）

| パス | 用途 | 生成 |
|---|---|---|
| `<ルート>/.claude/harness/config/` | 設定（`common.yml` ＋ 各プラグイン YAML） | 手動 |
| `<ルート>/.claude/harness/logs/` | hook 処理ログ（`<yyyy-MM-dd>.jsonl`） | 自動 |

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

- ファイルが**欠落・破損**しても既定値で起動する（警告ログを 1 行出す）。
- 未知のキーは無視される（前方互換）。
- 値の解釈: `logLevel` は大文字小文字を区別しない。`maxParallel` は 0 以下を既定へフォールバック。

### tools（プラグインの有効化）

`lib/`（共有）に導入したプラグインを、このプロジェクトで **PluginName** 単位に on/off する。キーは各プラグインの `PluginName`（DLL 名でも AssemblyName でもなく、`PluginBase.PluginName` の値）。

- `true` のプラグインのみ有効化。起動時にログへ `<PluginName>: 起動しました` を記録する。
- `false` および**未記載**のプラグインは無効（一切発火しない）。`tools` セクション自体が無い／空なら**全プラグインが off**。
- `tools` に書いた PluginName が `lib/` に存在しない場合は、**そのプロジェクトでは無視**（エラーログを 1 行残す）。daemon は他プロジェクトの処理を続ける。

```yaml
tools:
  - ai-harness-deny: true      # 有効化（起動ログを出す）
  - ai-harness-inject: false   # 無効（除外）
  # 記載しないプラグインも無効
```

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

`lib/` のプラグイン DLL を差し替えた場合のみ、型を読み直すため daemon の再起動が必要。

```sh
ai-harness-main --restart   # lib の DLL 差し替えを反映
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
