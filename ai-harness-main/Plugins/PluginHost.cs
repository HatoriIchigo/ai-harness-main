using System.Text.Json.Nodes;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>集約後の最終判定。</summary>
/// <param name="ExitCode">0=許可／非0=deny。</param>
/// <param name="Reason">deny 理由（集約）。</param>
/// <param name="AdditionalContext">Claude へ注入する追加コンテキスト（集約、非ブロック）。無ければ null。</param>
internal sealed record HostDecision(int ExitCode, string? Reason, string? AdditionalContext = null)
{
    public bool IsDeny => ExitCode != 0;
}

/// <summary>
/// 1 リクエストの発火結果。deny 判定（<see cref="Decision"/>）と、各プラグインが返した
/// state スライス（PluginName → 新しい値。返さなかった／変更なしのプラグインは含まない）。
/// </summary>
/// <param name="Decision">deny 先勝ちで集約した最終判定。</param>
/// <param name="StateUpdates">名前空間ごとの state 更新。<see cref="StateStore.ApplyAndSave"/> へ渡す。</param>
/// <param name="Denies">deny したプラグインごとの監査レコード（許可なら空）。</param>
internal sealed record HostOutcome(
    HostDecision Decision,
    IReadOnlyDictionary<string, JsonNode> StateUpdates,
    IReadOnlyList<DenyEvent> Denies);

/// <summary>
/// 発見済みプラグイン型から、リクエスト毎にインスタンスを生成して並列発火し、
/// 結果を deny 先勝ちで集約するホスト。インスタンスはリクエスト毎に作る（隔離維持・モデル b）。
/// </summary>
internal sealed class PluginHost
{
    private readonly Action<LogEntry> _log;
    private readonly int _maxParallel;
    private readonly string _configDir;

    /// <param name="configDir">このプロジェクトの設定ディレクトリ。各プラグインの <c>LoadConfig</c> に渡す。</param>
    public PluginHost(Action<LogEntry> log, int maxParallel, string configDir)
    {
        _log = log;
        _maxParallel = Math.Max(1, maxParallel);
        _configDir = configDir;
    }

    private static readonly IReadOnlyDictionary<string, JsonNode> EmptyUpdates =
        new Dictionary<string, JsonNode>();

    public async Task<HostOutcome> RunAsync(
        IReadOnlyList<Type> pluginTypes, HookData data, CancellationToken ct = default)
    {
        if (pluginTypes.Count == 0)
        {
            return new HostOutcome(new HostDecision(0, null), EmptyUpdates, []);
        }

        using var gate = new SemaphoreSlim(_maxParallel);

        var tasks = pluginTypes.Select(t => RunOneAsync(t, data, gate, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var decision = Aggregate(results.Select(r => r.Result));

        // 各プラグインが返した state スライスを PluginName で束ねる（null＝変更なしは除外）。
        var updates = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var (name, result, _) in results)
        {
            if (result.State is { } slice)
            {
                updates[name] = slice; // 規約上 PluginName は一意。万一重複しても後勝ち。
            }
        }

        // deny したプラグインごとに監査レコードを起こす。deny 先勝ちで集約された後でも、
        // 「どのプラグインがなぜ止めたか」は個別に残す（集約後の理由文字列からは復元できない）。
        var denies = results
            .Where(r => r.Result.ExitCode != 0)
            .Select(r => new DenyEvent(
                r.Name, r.Kind,
                r.Result.Reason ?? "プラグインにより拒否",
                data.ToolName, data.HookEventName))
            .ToList();

        return new HostOutcome(decision, updates, denies);
    }

    private async Task<PluginOutcome> RunOneAsync(
        Type type, HookData data, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Init/Action は同期 iterator。スレッドプールへ逃がして並列性を確保。
            return await Task.Run(() => Execute(type, data), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>1 プラグインの実行結果。<paramref name="Kind"/> は deny したときのみ意味を持つ。</summary>
    private readonly record struct PluginOutcome(string Name, PluginResult Result, DenyKind Kind);

    private PluginOutcome Execute(Type type, HookData data)
    {
        var result = new PluginResult();

        PluginBase plugin;
        try
        {
            plugin = (PluginBase)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            // フェイルクローズ: プラグインを生成できない＝検証できないので通さない。
            result.ExitCode = 2;
            result.Reason = $"{type.FullName} の生成に失敗（フェイルクローズ）: {ex.Message}";
            return new PluginOutcome(type.FullName ?? "unknown", result, DenyKind.FailClose);
        }

        var name = plugin.PluginName;

        // Tools/Events フィルタ: マッチしない場合は発火しない（ExitCode 0・State null のまま）。
        if (!plugin.ShouldFire(data))
        {
            return new PluginOutcome(name, result, DenyKind.Rule);
        }

        try
        {
            // 設定をこのインスタンスへロード（Action から Config を参照可能にする）。
            // 起動時に ProjectContext が検証済みのため通常成功。失敗時は下の catch でフェイルクローズ（ブロック）。
            plugin.LoadConfig(_configDir);
            // Init は ProjectContext がプロジェクトごとに1回実行済み。ここでは Action のみ。
            // 列挙完了で result.ExitCode / result.State が確定。
            foreach (var entry in plugin.Action(data, result))
            {
                _log(entry with { Source = name });
            }
        }
        catch (Exception ex)
        {
            // フェイルクローズ: プラグインが検証を完了できなかった（クラッシュ）場合は通さない。
            // 検証を通り抜けさせるとガードとして機能しないため、当該アクションをブロックする。
            result.ExitCode = 2;
            result.Reason = $"{name} の検証に失敗（フェイルクローズ）: {ex.Message}";
            return new PluginOutcome(name, result, DenyKind.FailClose);
        }
        // プラグインが自らルールで拒否した（例外ではない）。
        return new PluginOutcome(name, result, DenyKind.Rule);
    }

    /// <summary>
    /// 集約。deny は先勝ち（1 つでも非 0 なら全体 deny、理由は連結）。
    /// additionalContext は deny/allow を問わず全プラグイン分を連結し、非ブロックで Claude へ運ぶ。
    /// </summary>
    private static HostDecision Aggregate(IEnumerable<PluginResult> results)
    {
        var list = results as IReadOnlyList<PluginResult> ?? results.ToList();

        var context = string.Join(
            "\n",
            list.Select(r => r.AdditionalContext).Where(s => !string.IsNullOrWhiteSpace(s)));
        var additionalContext = string.IsNullOrWhiteSpace(context) ? null : context;

        var denied = list.Where(r => r.ExitCode != 0).ToList();
        if (denied.Count == 0)
        {
            return new HostDecision(0, null, additionalContext);
        }

        var reason = string.Join(
            "\n",
            denied.Select(r => r.Reason).Where(s => !string.IsNullOrWhiteSpace(s)));

        return new HostDecision(
            2, string.IsNullOrWhiteSpace(reason) ? "プラグインにより拒否" : reason, additionalContext);
    }
}
