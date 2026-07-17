# アーキテクチャ

ai-harness-main の内部構成と処理フロー。コンポーネント単位の責務と、リクエストがどう流れるかを示す。

## 全体像

単一バイナリ `ai-harness-main` が bridge（hook 受け口）と daemon（常駐サーバー）を兼ねる。
プラグインとコアは `ai-harness-baselib` の契約のみを共有し、互いを直接参照しない。

```
Claude Code ──hook──▶ ai-harness-main (bridge) ──named pipe──▶ ai-harness-main (daemon)
  プロジェクト A          cwd→ルート解決し封筒で中継            │（単一・共有）
  プロジェクト B  ──────────────────────────────────────────▶ │
                                                              ├─ lib/*.dll          共有プラグイン（型発見1回）
                                                              ├─ ProjectContext A   A の設定・ログ・有効プラグイン
                                                              ├─ ProjectContext B   B の設定・ログ・有効プラグイン
                                                              └─ ai-harness-baselib  共有契約

ai-harness-main  ──┐
                   ├──▶  ai-harness-baselib
各プラグイン DLL  ──┘
```

- **bridge** … hook が叩く受け口。cwd からプロジェクトルートを解決し、hook JSON を封筒で daemon へ中継。未起動なら daemon を自動起動。
- **daemon** … 単一・共有の常駐サーバー。型発見（1回）を保持し、プロジェクトルートをキャッシュキーにプロジェクト別へ振り分ける。
- **baselib** … `PluginBase` / `HookData` 等、境界を越える型のみを定義。

## 実行モード

`Program.cs` が第1引数で分岐する。

| 起動 | 役割 |
|---|---|
| （引数なし） | bridge。hook ごとに起動され、stdin の hook JSON を daemon へ中継。未起動なら daemon を detached 起動。stdin が端末・空なら hook ではないと判断し使い方を出して 1 |
| `--daemon` | 常駐サーバー。名前付きパイプで接続を待ち受け、プロジェクト別に処理 |
| `--ensure` | daemon 未起動なら detached 起動して終了 |
| `--stop` | 稼働中の daemon を停止 |
| `--restart` | daemon を停止後に再起動（`lib` のプラグイン DLL 差し替え反映用） |
| `--standalone` | daemon を介さず stdin の hook JSON を 1 件処理して終了（テスト・フォールバック） |
| `--update` | `config/plugins.yml` に従いプラグインを更新し、続けて本体自身も自己更新。詳細は [self-update.md](self-update.md) |
| `--update <プラグイン名>` | 指定した 1 プラグインのみ更新（リポジトリ名で照合）。daemon 再起動で反映。本体自己更新はしない |
| `--apply-update` | 内部モード。`--update` が publish した tmp の新バイナリが起動し、インストール先の実行体を置換 |
| `--health` | 起動検証用。ランタイムが正常起動すれば 0 を返す（自己更新の健全性判定に使う） |
| `--project` | 稼働中の daemon がメモリに展開しているプロジェクト一覧を表示 |
| `--logs [プロジェクト]` | ログ表示。無指定は実行体隣の `logs/`、指定時はそのプロジェクトの `.claude/harness/logs/` |
| `--plugin [プロジェクト]` | 無指定は `lib/` のプラグイン一覧、指定時はそのプロジェクトでの有効/無効 |
| `--validate [プロジェクト]` | 設定で hook が通る状態か検証（無指定は cwd から解決）。0=成功 / 1=失敗 |
| `--doctor` | 配置の診断（`lib`・native・`resources`・daemon・`git`/`dotnet`）。0=error なし / 1=error あり |
| `--fire [プラグイン名]` | cwd のプロジェクトで有効プラグインの能動スキャン（`Fire`）を daemon 経由で一斉起動（無指定は全プラグイン）。未起動なら daemon を起動。hook と独立のレポートでゲートではない。0=問題なし / 2=検出 / 1=接続・実行不能 |
| `--version` / `-v` | 版・ランタイム・実行体パスを表示する |
| `--help` / `-h` | 使い方を表示する |

bridge になるのは**引数なし**のときだけ。未知の引数は使い方を出して 1 で終わる（typo を hook として
扱わないため）。

引数なしでも、stdin が**端末**（人間が直接起動）または**空**なら hook 経由の起動ではないと判断し、中継せず
使い方を出して 1 で終わる。端末のときは stdin を読みにいかないので、入力待ちで固まらない。hook として
呼ばれる限り stdin にはリダイレクトされた JSON が必ず載るため、この 2 つは hook 経路では起きない。

情報表示モード（`--project`／`--logs`／`--plugin`）は hook 規約の外にあり、成功 0・引数エラー 1 を返す。
`--logs` は `--n <件数>`（新しい順に上位 N 件）・`--filter <レベル,…>`（`trace`／`debug`／`info`／`warn`／`error`）・
`--deny`（deny の監査レコードのみ）を取り、フィルタしてから件数を切る。`--project` は daemon へ照会するが
**起こさない**（未起動なら空一覧）。`--plugin` は daemon を介さず `lib/` と `common.yml` を直接読む。

## deny の記録

deny は集約されて Claude Code へ返るが、集約後の理由文字列からは「どのプラグインがなぜ止めたか」を
復元できない。そこで `PluginHost` は deny したプラグインごとに `DenyEvent` を起こし、`ProjectContext` が
1 件ずつ構造化ログとして記録する（`event=deny`／`kind`／`plugin`／`tool`／`hookEvent`／`reason`）。

由来を `DenyKind` で 2 つに分ける。**`rule`**（プラグインがルールに従って拒否＝設計どおり、`warn`）と、
**`failclose`**（生成失敗・クラッシュ・`common.yml` 不正など検証できずにブロック＝ハーネス側の異常、`error`）。
両者は監査上まったく意味が違うため、レベルで区別して `--logs --deny --filter error` が前者を拾わないようにする。

## コンポーネント

ソースは責務別ディレクトリに分割。namespace は全て `ai_harness_main`（フラット）。

| ファイル | 責務 |
|---|---|
| `Program.cs` | エントリ。モード分岐と standalone 処理 |
| `Cli/CliOptions.cs` | 情報表示モードの引数解釈（プロジェクト位置引数・`--n`・`--filter`） |
| `Cli/ProjectsCommand.cs` | `--project`。daemon へ照会して一覧表示 |
| `Cli/LogsCommand.cs` | `--logs`。ログディレクトリを解決して表示 |
| `Cli/PluginsCommand.cs` | `--plugin`。`lib/` の発見結果と `common.yml` のトグルを突き合わせ。`--enable`/`--disable` で書き換え |
| `Cli/CommonYamlEditor.cs` | `common.yml` の `tools` を行単位で最小編集（コメント・キー順を保つ） |
| `Cli/LogReader.cs` | `*.jsonl` を新しい順に読む（追記中でも読めるよう共有オープン） |
| `Cli/TextTable.cs` | `a \| b` 形式の等幅テーブル出力 |
| `Cli/ValidateCommand.cs` | `--validate`。daemon と同じ起動検証を hook 抜きで実行 |
| `Cli/DoctorCommand.cs` | `--doctor`。診断項目を集約して表示・終了コード決定 |
| `Cli/DoctorProbes.cs` | 個々の診断（`lib`・native の実ロード・daemon 照会・外部コマンド） |
| `Cli/VersionCommand.cs` | `--version`。版・ランタイム・実行体パス |
| `Cli/Usage.cs` | `--help` と不明引数エラーで出す使い方 |
| `Bridge/Bridge.cs` | bridge モード。cwd→ルート解決、封筒生成、daemon へ送信、応答出力、未起動時の起動 |
| `Bridge/ProjectLocator.cs` | cwd から `.claude` を上方探索しプロジェクトルートを決定 |
| `Plugins/PluginLoader.cs` | `lib/*.dll` を走査し `PluginBase` 派生型を発見 |
| `Plugins/PluginRegistry.cs` | 共有型レジストリ。起動時 1 回の型発見を保持（全プロジェクト共有） |
| `Plugins/ProjectContext.cs` | プロジェクト別の検証・`Init`・発火・設定ホットリロード・最終アクセス管理 |
| `Plugins/StartupValidation.cs` | 起動検証の結果（発火対象の型＋有効化したのに起動できなかった理由） |
| `Plugins/PluginHost.cs` | リクエスト毎にインスタンス生成・並列発火・deny 先勝ち集約 |
| `Plugins/PluginLoadContext.cs` | プラグイン DLL 用 ALC。baselib は既定 ALC へ委ね型同一性を保つ。管理依存は `lib/`、ネイティブは `runtimes/<rid>/native/` を直接プローブ |
| `Ipc/Daemon.cs` | 常駐サーバ。マルチテナント振り分け・アイドル回収・自動停止・ensure／stop／restart |
| `Ipc/RequestEnvelope.cs` | bridge／CLI→daemon の封筒（`type`＝`hook`／`stop`／`projects`、`projectRoot`、`hookJson`） |
| `Ipc/HarnessPipe.cs` | パイプ名生成（実行体ディレクトリの SHA256） |
| `Ipc/HookOutput.cs` | Claude へ返す hook 出力 JSON（`hookSpecificOutput.additionalContext`）の組み立て |
| `Ipc/HookResponse.cs` | daemon→bridge の応答ペイロード |
| `Ipc/ProjectsResponse.cs` | daemon→CLI（`--project`）の応答ペイロード |
| `Ipc/DaemonClient.cs` | CLI から daemon への照会。daemon は起動しない |
| `Ipc/Framing.cs` | 長さ前置フレーミング |
| `Config/InstallPaths.cs` | 実行体基準のグローバルパス（`lib`／`run`／グローバル log） |
| `Config/ProjectConfig.cs` | プロジェクト個別設定（`<ルート>/.claude/harness/config/common.yml` ロード） |
| `Logging/Logger.cs` | レベルフィルタ＋ログファイルへ集約（出力先ディレクトリは引数）。構造化フィールドの付加 |
| `Logging/DenyEvent.cs` | deny の監査レコード（由来＝`rule`／`failclose`、対象ツール、理由） |

## マルチテナント

単一の daemon が複数プロジェクトをさばく。`lib`（プラグイン型）は全プロジェクト共有、設定・ログ・有効プラグイン集合はプロジェクトごとに分離する。

- **型発見**: daemon 起動時に `lib/` を 1 回走査し、`PluginRegistry` が型一覧を保持。
- **プロジェクト識別**: bridge が自プロセスの cwd からルート（`.claude` を含む階層）を解決し、封筒に詰めて渡す。daemon は常駐ゆえ各 hook の cwd を持たないため、識別は bridge が担う。
- **キャッシュ**: daemon はルートをキーに `ProjectContext` を遅延生成・キャッシュ（`Lazy` で二重生成を防止）。初回リクエストでそのプロジェクトの `common.yml`（有効化トグル）と各プラグイン YAML（個別設定）を読み、検証・`Init` を済ませる。

## プラグインのライフサイクル

型発見（重い処理）はプロセス寿命で1回・全プロジェクト共有。検証・`Init`・発火はプロジェクトごと。発火はリクエスト毎にインスタンスを作り直す（隔離維持・ステートレス前提）。

```
[daemon 起動時 = プロセス寿命で1回]
  PluginLoader.DiscoverTypes(lib/)      … *.dll 走査して PluginBase 派生型を収集
        ▼
  PluginRegistry                        … 型一覧を保持（全プロジェクト共有）

[プロジェクト初回アクセス時 = ルートごとに1回]
  ProjectContext.Create(types, root)    … 型ごとに1インスタンス生成し:
        ├─ common.yml の tools で有効化されたもののみ対象
        ├─ Tools / Events を検証（NG なら除外）
        ├─ LoadConfig(configDir)（ConfigName 必須。失敗で除外）
        └─ Init() を1回実行しログ
        ▼
  有効プラグイン型のリストを保持＋設定 YAML を FileSystemWatcher で監視

[hook 発火時 = リクエスト毎]
  PluginHost.RunAsync(types, data)
        ├─ 型ごとに新インスタンスを生成（隔離）
        ├─ ShouldFire(data) で発火判定（OR 評価）
        ├─ LoadConfig(configDir)（Action から Config 参照可に）
        ├─ Action(data, result) を列挙 → result.ExitCode 確定
        └─ 全結果を Aggregate（deny 先勝ち）
```

- `Init` を実行するインスタンスと `Action` のインスタンスは**別**。状態は持ち越さない。
- プラグインのクラッシュ（`LoadConfig`／`Action` の例外）や生成失敗は**フェイルクローズ**（当該プラグインを deny 扱いにしてブロック）。検証を完了できないアクションを素通りさせないため。
- `Tools`/`Events`/`FileNames`/`BashCommands` が全て `null` のプラグインは発火条件が無く、除外はされないが一切発火しない（起動時に警告ログ）。

### 起動検証のフェイルクローズ

`common.yml` の `tools` で**有効化した**プラグインが発火できる状態に到達できなかった場合、それを除外して
素通りさせるとガードが消える。`ValidateAndInit` は理由を `StartupValidation.Errors` へ積み、`RunAsync` が
そのプロジェクトの hook をブロックする。対象は次の 4 つ。

| 事象 | 扱い |
|---|---|
| `tools` で有効化したが `lib` に存在しない | ブロック |
| `Tools`／`Events` の検証に失敗 | ブロック |
| `LoadConfig` に失敗（設定 YAML が無い・壊れている） | ブロック |
| `Init` が例外を投げた | ブロック |

インスタンス生成に失敗した型は `PluginName` を取れず有効/無効を判定できないため、**有効かもしれないものを
検証できなかった**として同じくブロックする。`tools: false`／未記載のプラグインは検証対象外（素通り）。
設定を直せばホットリロードでコンテキストが再構築され、ブロックは解除される。

## ホットリロード

各 `ProjectContext` は自身の設定ディレクトリ（`<ルート>/.claude/harness/config`）の `*.yml` を `FileSystemWatcher` で監視する。

- `common.yml`・各プラグイン YAML の変更を検知し、デバウンス（連続イベントを束ねる）後に当該プロジェクトの有効プラグイン集合を再構築（設定再ロード・`Init` 再実行）。
- 再構築結果は原子的に差し替え、実行中リクエストは差し替え前の集合を使い続ける。
- `lib` の DLL 差し替えはホットリロード対象外。`--restart` で型レジストリごと作り直す。

## アイドル回収と自動停止

Claude Code 終了後に daemon が居座らないよう、2 段で回収する。

- **プロジェクト回収**: スイーパが定期走査（既定 60 秒間隔）し、無アクセスが一定時間（既定 30 分）を超えたプロジェクトの `ProjectContext` を破棄（`FileSystemWatcher` も dispose）。
- **自動停止**: 全プロジェクトが回収されメモリ（キャッシュ）が空になった時点で daemon 自体を終了。加えて、接続が一定時間（既定 5 分）全く無ければ受付ループが起きて生存プロジェクトの有無を確認し、空なら終了する（生存していれば待受を続けるため、これは保持時間の上限ではない）。

各時間は `<実行体>/config/daemon.yml` で変えられる（`evictAfterMinutes` / `idleShutdownMinutes` / `sweepIntervalSeconds`）。起動時に 1 回だけ読むため、変更の反映には `--restart` が要る。詳細は [configuration.md](configuration.md)。

## リクエスト処理フロー

1. Claude Code の hook → `ai-harness-main`（bridge）を起動
2. bridge が cwd から `.claude` を上方探索してプロジェクトルートを解決
3. bridge が名前付きパイプで daemon へ接続（未起動なら `--daemon` を detached 起動して再接続）
4. bridge が封筒 `{ type, projectRoot, hookJson }` をフレーム送信
5. daemon が `projectRoot` で `ProjectContext` を取得（初回は生成）、`HookData.Parse` で解析し `PluginHost` が全プラグインを並列発火（`maxParallel` で同時数制限）
6. 各 `PluginResult` を **deny 先勝ち**で集約（1 つでも `ExitCode != 0` なら全体 deny、理由を改行連結）
7. daemon が `{ exitCode, reason, additionalContext }` を応答 → bridge が deny 理由を stderr、additionalContext を hook 出力 JSON で stdout、その exit code で終了

## 終了コード

bridge（hook）が Claude Code へ返す終了コードは 2 値。

| コード | 意味 |
|---|---|
| `0` | 許可（正常）。**例外的に** bridge が daemon にまったく接続できない（基盤ごと停止）ときも、ロックアウト回避のため 0（fail-open） |
| `2` | deny（ブロック）。理由を stderr へ。プラグインによる deny のほか、**内部エラー・不正入力・`common.yml` 不正など「検証できなかった」場合のフェイルクローズもここに倒す** |

daemon 経路・`--standalone` とも同じ方針（内部エラーは 2＝ブロック）。唯一 fail-open で 0 を返すのは、bridge が daemon に接続できないケースのみ。

## IPC（プロセス間通信）

- **トランスポート**: 名前付きパイプ。Windows＝ネイティブ名前付きパイプ、Unix＝UDS 実装。コードは単一（OS 差は .NET が吸収）。
- **パイプ名**: 実行体ディレクトリ（`AppContext.BaseDirectory`）の SHA256 先頭 16 hex から決定的に生成（`ai-harness-<hash>`）。bridge と daemon は同一実行体から起動されるため同名を算出し、インストール単位で 1 つの共有 daemon に対応する。
- **フレーミング**: 長さ前置（int32 LE）。1 フレーム上限 64 MiB。
- **リクエスト**: `RequestEnvelope`（UTF8 JSON）。`type=hook` は `projectRoot` ＋ `hookJson`、`type=stop` は停止要求。
- **応答**: `HookResponse`（UTF8 JSON）。`exitCode` ＋ `reason` ＋ `additionalContext`。

## 多重起動抑止

`run/daemon.lock` を `FileShare.None` で開いて排他する（best-effort）。`--ensure` と bridge のパイプ疎通チェックで大半の競合を回避する。

## 設定の反映タイミング

- **設定 YAML（`common.yml`・各プラグイン YAML）**: ホットリロード対象。保存すると当該プロジェクトが再構築される。
- **プラグイン DLL（`lib/`）**: daemon 起動時に 1 回読む。差し替え反映には `--restart` が必要。

詳細は [configuration.md](configuration.md) を参照。
