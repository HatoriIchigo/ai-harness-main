# プラグイン開発ガイド

新しい拡張プラグインを作成し、ハーネスへ組み込むまでの流れ。プラグインは `ai-harness-baselib` の `PluginBase` を継承した DLL として実装し、`lib/` に配置する。`ai-harness-main` は参照しない。

## 全体の流れ

```
1. プロジェクト作成（クラスライブラリ）
2. baselib を参照
3. PluginBase を継承して実装
4. 設定ファイル（YAML）を用意
5. ビルドして DLL を生成
6. DLL を lib/、YAML を config/ へ配置
7. daemon を --restart で再起動して反映
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

    // 設定ファイル名（必須）。config/<この名前> を読む
    public override string ConfigName => "my-plugin.yml";

    // 起動時に1度だけ実行（daemon 寿命で1回）
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

## ログの返し方

`Init`／`Action` は `IEnumerable<LogEntry>` を `yield` で逐次返す。`source` は設定不要（main が `PluginName` を打刻）。

```csharp
yield return LogEntry.Trace("詳細");
yield return LogEntry.Debug("デバッグ");
yield return LogEntry.Info("情報");
yield return LogEntry.Warning("警告");
yield return LogEntry.Error("エラー");
```

`LogLevel` は `Trace < Debug < Info < Warning < Error`。`config/main.yml` の `logLevel` 閾値以上のみが `logs/<日付>.jsonl` に出力される。

## 3. 設定ファイル（必須）

`ConfigName` は必須。宣言したら**同名の YAML を `config/` に置く**こと。ファイルが無い／読めないとそのプラグインは起動時に無効化される（フェイルクローズ）。設定値が不要でも空ファイルを置けば空マッピングとして扱われる。

```yaml
# config/my-plugin.yml
marker: "DENYME"
```

プラグイン側からは `Config`（`IReadOnlyDictionary<string, object>`）で参照する。スカラは `string`、ネストは `Dictionary<object, object>`、配列は `List<object>`（YamlDotNet の既定）。プラグインは YamlDotNet に依存しない。

## 4. ビルドと配置

```sh
dotnet build sample-plugins/MyPlugin/MyPlugin.csproj -c Release

# 生成された DLL を lib/ へ、設定 YAML を config/ へ
cp sample-plugins/MyPlugin/bin/Release/net10.0/MyPlugin.dll  <配置先>/lib/
cp config/my-plugin.yml                                       <配置先>/config/

# daemon を再起動して反映
<配置先>/ai-harness-main --restart
```

詳細なビルド・発行手順は [build-and-deploy.md](build-and-deploy.md) を参照。

## 動作確認

standalone モードなら daemon を介さず単体で叩ける（hook JSON を stdin へ）。

```sh
echo '{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"echo DENYME"}}' \
  | <配置先>/ai-harness-main
echo "exit=$?"   # deny なら 2
```

ログは `logs/<yyyy-MM-dd>.jsonl` に集約される。発火しない場合は発火条件（`Tools`/`Events`）と設定ファイルの配置を確認する。

## 参考実装

`sample-plugins/` に動作するサンプルがある。

| サンプル | 内容 |
|---|---|
| `EventLogger` | 全イベントをメタ情報のみログ記録（deny しない） |
| `DenyMarker` | Bash コマンドが設定値を含む場合に deny（設定ファイル利用例） |
| `LogTester` | ログレベル動作の検証 |

`ai-harness-baselib/ai-harness-baselib/Examples/BlockDangerousBashPlugin.cs` にも実装例がある。
