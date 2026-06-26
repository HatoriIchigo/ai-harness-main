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
/// 発見済みプラグイン型から、リクエスト毎にインスタンスを生成して並列発火し、
/// 結果を deny 先勝ちで集約するホスト。インスタンスはリクエスト毎に作る（隔離維持・モデル b）。
/// </summary>
internal sealed class PluginHost
{
    private readonly Action<LogEntry> _log;
    private readonly int _maxParallel;

    public PluginHost(Action<LogEntry> log, int maxParallel)
    {
        _log = log;
        _maxParallel = Math.Max(1, maxParallel);
    }

    public async Task<HostDecision> RunAsync(
        IReadOnlyList<Type> pluginTypes, HookData data, CancellationToken ct = default)
    {
        if (pluginTypes.Count == 0)
        {
            return new HostDecision(0, null);
        }

        using var gate = new SemaphoreSlim(_maxParallel);

        var tasks = pluginTypes.Select(t => RunOneAsync(t, data, gate, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return Aggregate(results);
    }

    private async Task<PluginResult> RunOneAsync(
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

    private PluginResult Execute(Type type, HookData data)
    {
        var result = new PluginResult();

        PluginBase plugin;
        try
        {
            plugin = (PluginBase)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            _log(LogEntry.Error($"インスタンス生成失敗 ({type.FullName}): {ex.Message}"));
            return result;
        }

        var name = plugin.PluginName;

        // Tools/Events フィルタ: マッチしない場合は発火しない（ExitCode 0 のまま）。
        if (!plugin.ShouldFire(data))
        {
            return result;
        }

        try
        {
            // 設定をこのインスタンスへロード（Action から Config を参照可能にする）。
            // 起動時に HarnessCore が検証済みのため通常成功。失敗はフェイルオープン（ログのみ）。
            plugin.LoadConfig();
            // Init は HarnessCore が起動時に型ごと1回実行済み。ここでは Action のみ。
            // 列挙完了で result.ExitCode が確定。
            foreach (var entry in plugin.Action(data, result))
            {
                _log(entry with { Source = name });
            }
        }
        catch (Exception ex)
        {
            // フェイルオープン: プラグインのクラッシュではブロックしない（ログのみ）。
            _log(LogEntry.Error($"実行失敗: {ex.Message}") with { Source = name });
        }
        return result;
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
