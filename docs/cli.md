# コマンドラインリファレンス

`ai-harness-main` は単一バイナリで、**第 1 引数のモードで役割が変わる**。
bridge（hook の受け口）になるのは**引数なし**のときだけで、未知の引数は使い方を出して `1` で終わる
（typo を hook として扱わないため）。

```
ai-harness-main [モード] [オプション]
```

導入手順は [quickstart.md](quickstart.md)、設定ファイルの仕様は [configuration.md](configuration.md) を参照。

## モード一覧

| モード | 役割 | 終了コード |
|---|---|---|
| （引数なし） | **bridge**。hook の受け口。stdin の hook JSON を daemon へ中継する。未起動なら daemon を起動 | 0=許可 / 2=deny |
| [`--daemon`](#--daemon) | 常駐サーバー。パイプで待ち受け、プロジェクト別に処理する | 0 |
| [`--ensure`](#--ensure--restart--stop) | 未起動なら detached 起動する | 0 |
| [`--restart`](#--ensure--restart--stop) | 停止→起動（`lib/` の DLL 差し替え反映） | 0 |
| [`--stop`](#--ensure--restart--stop) | 稼働中の daemon を停止する | 0 |
| [`--standalone`](#--standalone) | daemon を介さず stdin を 1 件処理して終了する | 0=許可 / 2=deny |
| [`--update`](#--update) | 全プラグインと本体を更新する | 0 / 非 0 |
| [`--update <プラグイン名>`](#--update-プラグイン名) | 指定した 1 プラグインのみ更新する | 0 / 非 0 |
| [`--validate`](#--validate-プロジェクト) | 設定で hook が通る状態か検証する | 0=成功 / 1=失敗 |
| [`--doctor`](#--doctor) | この配置でハーネスが機能するか診断する | 0=致命的問題なし / 1=error あり |
| [`--project`](#--project) | daemon がメモリに展開しているプロジェクト一覧 | 0 / 1=引数エラー |
| [`--logs`](#--logs-プロジェクト-オプション) | ログを新しい順に表示する | 0 / 1=引数エラー |
| [`--plugin`](#--plugin-プロジェクト) | プラグイン一覧／プロジェクトでの有効状態 | 0 / 1=引数エラー |
| [`--fire`](#--fire-プラグイン名) | 有効プラグインの能動スキャンを起動する | 0=問題なし / 2=検出 / 1=実行不能 |
| [`--health`](#--health) | 起動検証（ランタイムが正常起動すれば 0） | 0 |
| `--version`, `-v` | 版・ランタイム・実行体パスを表示する | 0 |
| `--help`, `-h` | 使い方を表示する | 0 |

> `--apply-update` は `--update` が内部的に使うモード（新バイナリから実行体を置換する）。利用者が直接叩くものではない。

### 終了コードの体系

3 系統あり、混ぜて解釈しない。

| 系統 | 対象 | 意味 |
|---|---|---|
| **hook 規約** | 引数なし（bridge）・`--standalone` | `0`=許可 / `2`=deny。内部エラー・不正入力・検証不能は**フェイルクローズで 2** に倒す。例外は bridge が daemon にまったく接続できないときだけで、全ツールのロックアウトを避けるため `0`（許可）で継続する |
| **コマンド規約** | `--validate`・`--doctor`・情報表示系 | `0`=成功 / `1`=失敗・引数エラー |
| **スキャン規約** | `--fire` | `0`=問題なし / `2`=いずれかのプラグインが検出 / `1`=接続・実行不能。`2` は「差し戻し」ではなくスキャン結果の集約 |

## daemon 制御

### `--daemon`

常駐サーバーとして前面起動する。名前付きパイプで待ち受け、プロジェクトごとに状態を持って処理する
（マルチテナント）。通常は直接叩かず、bridge や `--ensure` が必要に応じて detached 起動する。

無アクセス 5 分でプロジェクト状態を破棄し、全プロジェクトが回収されて空になれば daemon 自体が終了する。
接続が 5 分途絶えた場合も終了する。ログは `<実行体>/logs/` にのみ出力する（stderr に消費者がいないため）。

### `--ensure` / `--restart` / `--stop`

```sh
ai-harness-main --ensure    # 未起動なら detached 起動
ai-harness-main --restart   # 停止 → 起動。lib/ の DLL 差し替えを反映する
ai-harness-main --stop      # 停止
```

**設定 YAML（`common.yml`・各プラグイン YAML）はホットリロードされるので `--restart` は不要。**
`lib/` のプラグイン DLL を差し替えたときだけ、型を読み直すために `--restart` が要る。

### `--standalone`

daemon を介さず、stdin の hook JSON を 1 件だけ in-process で処理して終了する。テストとフォールバック用。
daemon 経路と違い、ログは stderr にも出る。

```sh
echo '{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"rm -rf /"}}' \
  | ai-harness-main --standalone
```

## 更新

### `--update`

`<実行体>/config/plugins.yml` に従い、拡張プラグインを `<実行体>/repos/` へ clone／build して `lib/` へ配置し、
続けて本体自身も publish して置換する（自己更新）。配置後は稼働中の daemon を自動再起動する。

- `git`／`dotnet` が PATH に無ければ**何もせず異常終了**（非 0）。
- baselib をプラグインより先に `repos/ai-harness-baselib` へ用意する（各プラグインの csproj が兄弟ディレクトリを相対参照するため）。
- `lib/` に入れただけでは発火しない。プロジェクトの `common.yml` の `tools` で有効化する。

定義ファイル（`plugins.yml`）の書式は [configuration.md](configuration.md)、内部設計は
[self-update.md](self-update.md) を参照。

### `--update <プラグイン名>`

指定した 1 プラグインだけを更新する。名前は `plugins.yml` の各エントリの**リポジトリ名**（URL 末尾）で照合する。

```sh
ai-harness-main --update ai-harness-file-rules
```

- 本体（`ai-harness-main` 自身）の自己更新は**行わない**。
- clone／build して `lib/` へ配置したあと、新しい DLL を反映するため稼働中の daemon を再起動する。
- 名前が `plugins.yml` のどれとも一致しなければ、指定できる名前を示して異常終了（非 0）。

### `--health`

ランタイムが正常起動できれば `ai-harness-main OK` を出して 0 を返すだけのモード。
自己更新が新バイナリの生存を確認し、ロールバックするかを判定するために使う。

## 検証

### `--validate [プロジェクト]`

**設定が壊れた瞬間にそのプロジェクトの全 hook がブロックされる**（フェイルクローズ）ため、
hook を待たずに同じ判定（`ProjectContext.ValidateAndInit`）を通し、結果を終了コードで返す。
daemon には触れず、プロジェクトのログも書かない。commit hook や CI から使える。

```sh
ai-harness-main --validate                    # cwd からプロジェクトルートを解決
ai-harness-main --validate C:\Users\project1  # 明示指定
```

| 結果 | 条件 |
|---|---|
| 0（成功） | 設定が正しい。または `common.yml` が無い（＝ハーネス対象外なので hook は素通り） |
| 1（失敗） | `common.yml` が壊れている／`tools` で有効化したプラグインが `lib/` に無い・設定 YAML が無い・`Init` に失敗する |

`--n` / `--filter` / `--deny` は受け付けない。

### `--doctor`

**この配置でハーネスが機能するか**を診断する。プロジェクト設定ではなくインストール環境を見る。

- `lib/` の DLL
- tree-sitter の native grammar（実際にロードを試す）
- `resources/`・ログ出力先
- daemon の稼働
- `--update` が要求する `git`／`dotnet`

判定は「失敗すると何が壊れるか」で決まる。プラグインが 1 つも動かなくなる `lib/` の欠落は **error（1）**、
tree-sitter プラグインや自己更新だけが使えなくなるものは **warn（0）**。native の欠落は「AST 解析に失敗して
フェイルクローズ」という遠回りな症状で表れるため、ここで直接切り分ける。

引数・オプションは取らない。

## 情報表示

ハーネスの動作には影響しない読み取り専用のモード。`ai-harness-tui` はこの 3 つを子プロセスとして叩いている。

### `--project`

稼働中の daemon がメモリに展開しているプロジェクトの一覧を表示する（**daemon は起こさない**）。
引数・オプションは取らない。

### `--logs [プロジェクト] [オプション]`

ログを**新しい順**に表示する。プロジェクト無指定は実行体自身のログ（`<実行体>/logs/`＝daemon ライフサイクル）、
指定時はそのプロジェクトの `.claude/harness/logs/`（hook 処理）。

| オプション | 内容 |
|---|---|
| `--n <件数>` / `-n <件数>` | 上位 N 件（新しい順）。1 以上の整数 |
| `--filter <レベル,…>` | `trace` / `debug` / `info` / `warn` / `error`。カンマ区切り・複数トークン可 |
| `--deny` | deny の監査レコードだけを表示する |

**フィルタしてから件数を切る**ため、`--filter` 併用時の `--n` は「該当ログの上位 N 件」を意味する。

```sh
ai-harness-main --logs --n 35                             # 実行体のログを新しい順に 35 件
ai-harness-main --logs C:\Users\project1 --filter warn,error
ai-harness-main --logs C:\Users\project1 --deny           # deny の監査レコードだけ
ai-harness-main --logs C:\Users\project1 --deny --filter error
```

#### deny の監査レコード

deny は通常のログ行に加えて**構造化フィールド**（`event=deny` / `kind` / `plugin` / `tool` / `hookEvent` /
`reason`）つきで `logs/<日付>.jsonl` に記録される。`--deny` はこの行だけを拾う。

由来はレベルで分かれる。

| レベル | 由来 | 意味 |
|---|---|---|
| `warn` | **ルールによる deny** | 設計どおりの動作（ガードが効いた） |
| `error` | **フェイルクローズ** | プラグインを検証できずにブロックした＝ハーネス側の異常 |

したがって `--deny --filter error` は「ハーネスが不調でブロックした件」だけを、
`--deny --filter warn` は「ルールが実際に効いた件」だけを拾う。

### `--plugin [プロジェクト]`

プロジェクト無指定は `lib/` の**インストール済みプラグイン一覧**、指定時は**そのプロジェクトでの有効/無効**
（`common.yml` の `tools` に照らした結果）を表示する。`--n` / `--filter` / `--deny` は受け付けない。

```sh
ai-harness-main --plugin                      # lib/ に何が入っているか
ai-harness-main --plugin C:\Users\project1    # そのプロジェクトで何が有効か
```

## スキャン

### `--fire [プラグイン名]`

cwd から解決したプロジェクトに対し、有効プラグインの**能動スキャン**（`PluginBase.Fire`）を
daemon 経由で起動して結果を表示する。プラグイン名を与えるとその 1 つだけを対象にする（未指定は全有効プラグイン）。
daemon が未起動なら detached 起動して待つ。

**hook とは独立でゲートではない。** hook は「これから起きるアクション」を検査するが、`--fire` は
「いま存在する状態」をプラグインに走査させる。commit hook や CI から強制的に検査したいときに使う。

```sh
ai-harness-main --fire                      # 有効な全プラグインをスキャン
ai-harness-main --fire ai-harness-deny      # 1 つだけ
```

| 終了コード | 意味 |
|---|---|
| 0 | 問題なし（全プラグインが正常） |
| 2 | いずれかのプラグインが検出した |
| 1 | daemon に接続できない・実行不能 |

出力はプラグインごとのブロックで、`[ok]` / `[detected]`・`reason`・`context`・ログ行が並ぶ。
スキャンを実装した有効プラグインが 1 つも無ければその旨を表示して 0 を返す。

## そのほか

### `--version` / `-v`

版・ランタイム・実行体パスを表示する。PATH 解決が意図どおりか（どの実行体が起動しているか）の確認にも使う。

### `--help` / `-h`

使い方を表示する。不明な引数を与えた場合も同じ内容を stderr に出して `1` で終わる。
