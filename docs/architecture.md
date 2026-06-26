# アーキテクチャ

ai-harness-main の内部構成と処理フロー。コンポーネント単位の責務と、リクエストがどう流れるかを示す。

## 全体像

3 プロジェクトの一方向依存。プラグインとコアは `ai-harness-baselib` の契約のみを共有し、互いを直接参照しない。

```
Claude Code ──hook──▶ ai-harness-client ──named pipe──▶ ai-harness-main (daemon)
                          （薄いクライアント）              │
                                                          ├─ lib/*.dll      拡張プラグイン
                                                          └─ ai-harness-baselib  共有契約

ai-harness-main  ──┐
                   ├──▶  ai-harness-baselib
各プラグイン DLL  ──┘
```

- `ai-harness-client` … hook が実際に叩く実行体。stdin を daemon へ中継するだけ。未起動なら daemon を自動起動。
- `ai-harness-main` … daemon／standalone 本体。プラグインのロード・発火・集約を担う。
- `ai-harness-baselib` … `PluginBase` / `HookData` 等、境界を越える型のみを定義。

## 実行モード

`Program.cs` が第1引数で分岐する。

| 起動 | 役割 |
|---|---|
| （引数なし） | standalone。stdin の hook JSON を 1 件処理して終了（直接実行・テスト・フォールバック用） |
| `--daemon` | 常駐。名前付きパイプで接続を待ち受け、接続ごとに処理。idle 30 分で自動終了 |
| `--ensure` | daemon 未起動なら detached 起動して終了（SessionStart 等から） |
| `--stop` | 稼働中の daemon を停止 |
| `--restart` | daemon を停止後に再起動（プラグイン DLL・config の変更反映用） |

## コンポーネント

ソースは責務別ディレクトリに分割。namespace は全て `ai_harness_main`（フラット）。

| ファイル | 責務 |
|---|---|
| `Program.cs` | エントリ。モード分岐と standalone 処理 |
| `HarnessCore.cs` | 中核。プラグイン型の発見・検証・Init（起動時1回）＋リクエスト処理の入口 |
| `Plugins/PluginLoader.cs` | `lib/*.dll` を走査し `PluginBase` 派生型を発見（ウォーム保持） |
| `Plugins/PluginHost.cs` | リクエスト毎にインスタンス生成・並列発火・deny 先勝ち集約 |
| `Plugins/PluginLoadContext.cs` | プラグイン DLL 用 ALC。baselib は既定 ALC へ委ね型同一性を保つ |
| `Ipc/Daemon.cs` | 常駐サーバ・ensure・stop・restart・多重起動抑止 |
| `Ipc/HarnessPipe.cs` | パイプ名生成（実行体ディレクトリの SHA256） |
| `Ipc/Framing.cs` | 長さ前置フレーミング（client と同一仕様） |
| `Config/HarnessConfig.cs` | 固定ディレクトリ算出＋ `config/main.yml` ロード |
| `Logging/Logger.cs` | レベルフィルタ＋単一ログファイルへ集約 |

## プラグインのライフサイクル

ロード（重い処理）はプロセス寿命で1回、発火はリクエスト毎にインスタンスを作り直す（隔離維持・ステートレス前提）。

```
[daemon 起動時 = プロセス寿命で1回]
  PluginLoader.DiscoverTypes(lib/)      … *.dll 走査して PluginBase 派生型を収集
        │
        ▼
  HarnessCore.ValidateAndInit(types)    … 型ごとに1インスタンス生成し:
        ├─ Tools / Events を検証（NG なら除外）
        ├─ LoadConfig（ConfigName 必須。失敗で除外）
        └─ Init() を1回実行しログ
        ▼
  有効プラグイン型のリストを保持

[hook 発火時 = リクエスト毎]
  PluginHost.RunAsync(types, data)
        ├─ 型ごとに新インスタンスを生成（隔離）
        ├─ ShouldFire(data) で発火判定（OR 評価）
        ├─ LoadConfig（Action から Config 参照可に）
        ├─ Action(data, result) を列挙 → result.ExitCode 確定
        └─ 全結果を Aggregate（deny 先勝ち）
```

- `Init` を実行するインスタンスと `Action` のインスタンスは**別**。状態は持ち越さない。
- プラグインのクラッシュは**フェイルオープン**（ログのみ、ブロックしない）。
- `Tools`/`Events`/`FileNames`/`BashCommands` が全て `null` のプラグインは発火条件が無く、除外はされないが一切発火しない（起動時に警告ログ）。

## リクエスト処理フロー

1. Claude Code の hook → `ai-harness-client` を起動
2. client が名前付きパイプで daemon へ接続（未起動なら `--daemon` を detached 起動して再接続）
3. client が stdin の hook JSON をフレーム送信
4. daemon が `HookData.Parse` で解析し、`PluginHost` が全プラグインを並列発火（`maxParallel` で同時数を制限）
5. 各 `PluginResult` を **deny 先勝ち**で集約（1 つでも `ExitCode != 0` なら全体 deny、理由を改行連結）
6. daemon が `{int32 exitCode, UTF8 reason}` を応答 → client が reason を stderr、exitCode で終了

## 終了コード

client（hook）が Claude Code へ返す終了コードは 2 値。

| コード | 意味 |
|---|---|
| `0` | 許可（正常。daemon 接続不能時の fail-open も 0） |
| `2` | deny（ブロック）。理由を stderr へ |

standalone（`Program.cs` 直接実行）は加えて `1`（hook 解析失敗・内部エラー。非ブロッキング扱い）を返す。

## IPC（プロセス間通信）

- **トランスポート**: 名前付きパイプ。Windows＝ネイティブ名前付きパイプ、Unix＝UDS 実装。コードは単一（OS 差は .NET が吸収）。
- **パイプ名**: 実行体ディレクトリ（`AppContext.BaseDirectory`）の SHA256 先頭 16 hex から決定的に生成（`ai-harness-<hash>`）。daemon と client は同居するため同名になり、プロジェクト単位で分離される。
- **フレーミング**: 長さ前置（int32 LE）。1 フレーム上限 64 MiB。
- **応答**: `int32 exitCode` + UTF8 `reason`。
- **停止**: client から magic 文字列 `__AI_HARNESS_STOP__` を送ると daemon が終了。

## 多重起動抑止

`run/daemon.lock` を `FileShare.None` で開いて排他する（best-effort）。`--ensure` と client のパイプ疎通チェックで大半の競合を回避する。

## 設定の反映タイミング

`config/main.yml` もプラグイン DLL（`lib/`）も daemon 起動時に一度だけ読む。変更を反映するには daemon の再起動（`--restart`）が必要。

詳細は [configuration.md](configuration.md) を参照。
