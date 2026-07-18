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

/// <summary>1 プラグインの能動スキャン（<c>--fire</c>）結果。</summary>
/// <param name="Name">PluginName。</param>
/// <param name="Result"><see cref="PluginBase.Fire"/> の結果ホルダ（列挙完了時点の確定値）。</param>
/// <param name="Logs">Fire が yield したログ（source 打刻済み）。</param>
internal readonly record struct FireOutcome(
    string Name, PluginResult Result, IReadOnlyList<LogEntry> Logs);

/// <summary>
/// <c>--fire</c> の実行レポート。<see cref="Error"/> が非 null なら実行できなかった
/// （設定不備・フェイルクローズ状態等）ことを表し、<see cref="Plugins"/> は空。
/// </summary>
/// <param name="Error">実行できなかった理由。実行できたら null。</param>
/// <param name="Plugins">プラグインごとの結果。</param>
internal sealed record FireReport(string? Error, IReadOnlyList<FireOutcome> Plugins)
{
    public static FireReport Failed(string error) => new(error, []);

    public static FireReport Ok(IReadOnlyList<FireOutcome> plugins) => new(null, plugins);
}

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

    /// <summary>
    /// 有効プラグインの <see cref="PluginBase.Fire"/> を並列実行し、プラグインごとの結果を集める。
    /// <paramref name="pluginFilter"/>（PluginName）指定時はその 1 つだけを対象にする（他は結果に含めない）。
    /// hook 経路と違い <see cref="PluginBase.ShouldFire"/> フィルタも deny 集約も無い（<c>--fire</c> はレポート）。
    /// </summary>
    public async Task<IReadOnlyList<FireOutcome>> RunFireAsync(
        IReadOnlyList<Type> pluginTypes, string? pluginFilter, string projectRoot,
        CancellationToken ct = default)
    {
        if (pluginTypes.Count == 0)
        {
            return [];
        }

        using var gate = new SemaphoreSlim(_maxParallel);

        var tasks = pluginTypes.Select(t => RunFireOneAsync(t, pluginFilter, projectRoot, gate, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return results.Where(r => r.HasValue).Select(r => r!.Value).ToList();
    }

    private async Task<FireOutcome?> RunFireOneAsync(
        Type type, string? pluginFilter, string projectRoot, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Fire は同期 iterator。スレッドプールへ逃がして並列性を確保。
            return await Task.Run(() => ExecuteFire(type, pluginFilter, projectRoot), ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 1 プラグインの Fire を実行する。フィルタ名に一致しなければ <c>null</c>（結果に含めない）。
    /// hook のゲートではないため生成・Fire の失敗はブロックせず、非 0 の結果としてレポートへ載せるだけ。
    /// </summary>
    private FireOutcome? ExecuteFire(Type type, string? pluginFilter, string projectRoot)
    {
        PluginBase plugin;
        try
        {
            plugin = (PluginBase)Activator.CreateInstance(type)!;
        }
        catch (Exception ex)
        {
            // 生成できないと PluginName が取れずフィルタ判定できない。名前指定時は対象外として黙って除外し、
            // 全プラグイン対象（フィルタ無し）のときだけ「生成失敗」をレポートへ残す。
            if (pluginFilter is not null)
            {
                return null;
            }
            var failed = new PluginResult { ExitCode = 2, Reason = $"{type.FullName} の生成に失敗: {ex.Message}" };
            return new FireOutcome(type.FullName ?? "unknown", failed, []);
        }

        var name = plugin.PluginName;
        if (pluginFilter is not null && !string.Equals(name, pluginFilter, StringComparison.Ordinal))
        {
            return null;
        }

        var result = new PluginResult();
        var logs = new List<LogEntry>();
        try
        {
            plugin.LoadConfig(_configDir);
            // Action と違い Fire は同期バッチ処理なので、応答が届くまでブロックしてよい LSP 診断リクエスタを渡す
            // （Action では設定しない。HookData.LspDiagnostics のキャッシュ読み取りのみを使う）。
            plugin.FireLsp = new FireLspRequester(projectRoot);
            foreach (var entry in plugin.Fire(projectRoot, result))
            {
                var stamped = entry with { Source = name };
                logs.Add(stamped);
                _log(stamped);
            }
        }
        catch (Exception ex)
        {
            // スキャンの失敗はレポートに検出結果として残すのみ（何もブロックしない）。
            result.ExitCode = 2;
            result.Reason = $"{name} の Fire に失敗: {ex.Message}";
        }
        return new FireOutcome(name, result, logs);
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
