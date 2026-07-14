# プラグイン開発ガイド

新しい拡張プラグインを作成し、ハーネスへ組み込むまでの流れ。プラグインは `ai-harness-baselib` の `PluginBase` を継承した DLL として実装し、インストール先の `lib/`（全プロジェクト共有）に配置する。`ai-harness-main` は参照しない。設定 YAML は各プロジェクトの `.claude/harness/config/` に置く。

## 全体の流れ

```
1. プロジェクト作成（クラスライブラリ）
2. baselib を参照
3. PluginBase を継承して実装
4. 設定ファイル（YAML）を用意
5. ビルドして DLL を生成
6. DLL を <インストール先>/lib/ へ配置（共有）
7. プロジェクトの .claude/harness/config/ へ YAML を配置し、--plugin --enable で有効化
8. daemon を --restart で再起動して新 DLL を反映（YAML の変更はホットリロードで反映）
```

## 1. プロジェクト作成と csproj

クラスライブラリとして作成し、`ai-harness-baselib` を **出力にコピーしない** 形で参照する。baselib は host（`ai-harness-main`）が共有ロードするため、`lib/` に baselib.dll を置くと型同一性が壊れる。

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <RootNamespace>MyPlugin</RootNamespace>
    <AssemblyName>MyPlugin</AssemblyName>
    <!-- baselib は host が共有ロードするため出力にコピーしない -->
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\ai-harness-baselib\ai-harness-baselib\ai-harness-baselib.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

</Project>
```

> プラグイン固有の NuGet 依存（baselib 以外）は通常どおり参照してよい。その DLL は `lib/` のプラグイン DLL と同じ場所に置けば `AssemblyDependencyResolver` が解決する。

## 2. PluginBase を実装

最小構成。`PluginName` と `Init`／`Action` は必須。発火条件（`Tools` 等）のいずれかと `ConfigName` を宣言する。

```csharp
using ai_harness_baselib;

namespace MyPlugin;

public sealed class MyPlugin : PluginBase
{
    // ログの source として使われる一意キー（必須）
    public override string PluginName => "my-plugin";

    // 発火条件（後述）。ここでは Bash ツールのみ対象
    public override IReadOnlyList<string> Tools => new[] { "Bash" };

    // 設定ファイル名（必須）。プロジェクトの .claude/harness/config/<この名前> を読む
    public override string ConfigName => "my-plugin.yml";

    // プロジェクト初回アクセス時に1度だけ実行（設定ホットリロード時にも再実行）
    public override IEnumerable<LogEntry> Init()
    {
        yield return LogEntry.Info("初期化");
    }

    // hook 発火本体。列挙完了時に result.ExitCode が確定する
    public override IEnumerable<LogEntry> Action(HookData data, PluginResult result)
    {
        // 自己フィルタ: 対象外なら yield break（ExitCode 0 = 許可のまま）
        if (data.Event != HookEvent.PreToolUse || data.ToolName != "Bash")
        {
            yield break;
        }

        var command = data.ToolInput?["command"]?.GetValue<string>();
        yield return LogEntry.Debug($"検査: {command}");

        // 設定ファイルの値は Config から参照
        var marker = (Config.TryGetValue("marker", out var m) ? m?.ToString() : null) ?? "";

        if (command is not null && marker.Length > 0 && command.Contains(marker))
        {
            yield return LogEntry.Warning("マーカー検出。deny。");
            result.ExitCode = 2;                          // 非 0 = deny
            result.Reason = $"ブロック: {command}";       // deny 理由
        }
        // ここまで到達 = ExitCode 0（許可）
    }
}
```

## 発火条件（`ShouldFire`）

ハーネスは hook ごとに以下を **OR 評価**し、いずれかにマッチしたプラグインのみ `Action` を発火する。全て `null` のプラグインは一切発火しない。

| プロパティ | マッチ対象 | 形式 |
|---|---|---|
| `Tools` | `tool_name` | 完全一致。`"*"` で全ツール |
| `Events` | `hook_event_name` | 完全一致。`"*"` で全イベント |
| `FileNames` | file_path（`tool_input.file_path` 優先、無ければトップレベル `file_path`） | glob（`*`／`?`）。大文字小文字を無視 |
| `BashCommands` | `tool_input.command` | glob（`*`／`?`）。大文字小文字を区別 |

- `Tools` は起動時に検証される（既知の組み込みツール名・`mcp__` パターン・`"*"` のみ許容）。不正なら**そのプラグインは無効化**される。
- `Events` も同様に `HookEvent` 名・`"*"` で検証される。
- 発火後はさらに `Action` 内で `HookData` を見て自己フィルタできる（上例の `data.Event` チェック）。

## 結果の返し方（deny 先勝ち）

iterator は戻り値で int を返せないため、結果は引数 `PluginResult` に書く。`Action` の **列挙が完了した瞬間**（最後の `yield` の後）に `result.ExitCode` が確定する。

- `ExitCode == 0` … 許可（既定）
- `ExitCode != 0` … deny。`Reason` に理由を添える

ハーネスは全プラグインの結果を集約し、**1 つでも非 0 なら全体 deny**（理由は改行連結）。

## 能動スキャン（`Fire`・任意）

`Action` が hook 発火に紐づくのに対し、`Fire` は利用者が `ai-harness-main --fire`（対象を絞るなら `--fire <プラグイン名>`）で**手動起動**する能動スキャン。cwd から解決したプロジェクトに対し、有効化されているプラグインの `Fire` が daemon 経由で一斉に呼ばれる。

```csharp
public override IEnumerable<LogEntry> Fire(string projectRoot, PluginResult result)
{
    yield return LogEntry.Info("スキャン開始");
    // …projectRoot 配下を点検し所見を yield…
    result.ExitCode = 2;                 // 検出（レポート表示のみ。何もブロックしない）
    result.Reason = "検出内容";
}
```

- 既定は no-op（`virtual`）。スキャンを実装したいプラグインだけ override する。
- `ShouldFire` フィルタは通らず、`common.yml` の `tools` で有効なプラグインが一律に呼ばれる。`LoadConfig` 済みで呼ばれるため `Config` を参照できる。
- `projectRoot` はスキャン対象のプロジェクトルート（絶対パス）。daemon 常駐ゆえ cwd は使えないため、走査対象は引数で受け取る。
- **hook のゲートではない**。ここでの `ExitCode != 0` は何かをブロックするのではなく、スキャンの検出結果としてレポート（`--fire` の出力）に表示されるだけ。`Reason`／`AdditionalContext`／yield したログがそのまま並ぶ。
- ログ・結果の書き方は `Action` と同じ（列挙完了時に `result` 確定）。

## ログの返し方

`Init`／`Action` は `IEnumerable<LogEntry>` を `yield` で逐次返す。`source` は設定不要（main が `PluginName` を打刻）。

```csharp
yield return LogEntry.Trace("詳細");
yield return LogEntry.Debug("デバッグ");
yield return LogEntry.Info("情報");
yield return LogEntry.Warning("警告");
yield return LogEntry.Error("エラー");
```

`LogLevel` は `Trace < Debug < Info < Warning < Error`。プロジェクトの `common.yml` の `logLevel` 閾値以上のみが `.claude/harness/logs/<日付>.jsonl` に出力される。

## 3. 設定ファイル（必須）

`ConfigName` は必須。宣言したら**同名の YAML をプロジェクトの `.claude/harness/config/` に置く**こと。ファイルが無い／読めないとそのプラグインはそのプロジェクトで無効化される（フェイルクローズ）。設定値が不要でも空ファイルを置けば空マッピングとして扱われる。

```yaml
# .claude/harness/config/my-plugin.yml
marker: "DENYME"
```

プラグイン側からは `Config`（`IReadOnlyDictionary<string, object>`）で参照する。スカラは `string`、ネストは `Dictionary<object, object>`、配列は `List<object>`（YamlDotNet の既定）。プラグインは YamlDotNet に依存しない。

## 4. ビルドと配置

```sh
dotnet build sample-plugins/MyPlugin/MyPlugin.csproj -c Release

# 生成された DLL を共有 lib/ へ、設定 YAML はプロジェクトの config/ へ
cp sample-plugins/MyPlugin/bin/Release/net10.0/MyPlugin.dll  <インストール先>/lib/
cp my-plugin.yml                                             <プロジェクト>/.claude/harness/config/

# 新 DLL を反映してから、そのプロジェクトで有効化
ai-harness-main --restart
ai-harness-main --plugin <プロジェクト> --enable MyPlugin
```

設定 YAML（`my-plugin.yml`）を置く前に有効化しようとすると、フェイルクローズを避けるため `--enable` が拒否する。

DLL を差し替えたときだけ `--restart`。`common.yml`・各プラグイン YAML の変更はホットリロードで反映される。詳細なビルド・発行手順は [build-and-deploy.md](build-and-deploy.md) を参照。

## 動作確認

`--standalone` なら daemon を介さず単体で叩ける（hook JSON を stdin へ）。`.claude/harness/config` を持つプロジェクト内（cwd がその配下）で実行する。

```sh
cd <プロジェクト>
echo '{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"echo DENYME"}}' \
  | ai-harness-main --standalone
echo "exit=$?"   # deny なら 2
```

ログは `.claude/harness/logs/<yyyy-MM-dd>.jsonl` に集約される。発火しない場合は発火条件（`Tools`/`Events`）・`common.yml` の有効化・設定ファイルの配置を確認する。

## 参考実装

`sample-plugins/` に動作するサンプルがある。

| サンプル | 内容 |
|---|---|
| `EventLogger` | 全イベントをメタ情報のみログ記録（deny しない） |
| `DenyMarker` | Bash コマンドが設定値を含む場合に deny（設定ファイル利用例） |
| `LogTester` | ログレベル動作の検証 |

`ai-harness-baselib/ai-harness-baselib/Examples/BlockDangerousBashPlugin.cs` にも実装例がある。
