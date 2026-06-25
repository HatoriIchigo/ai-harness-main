using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// ハーネスの中核。プラグイン型の発見（1回）を保持し、リクエスト毎に発火・集約する。
/// standalone・daemon の双方から共通利用される。
/// </summary>
internal sealed class HarnessCore
{
    private readonly Logger _logger;
    private readonly int _maxParallel;
    private readonly IReadOnlyList<Type> _pluginTypes;

    public HarnessCore(Logger logger, string pluginDir, int maxParallel)
    {
        _logger = logger;
        _maxParallel = maxParallel;
        // アセンブリのロード・走査はここで1回だけ（daemon ではプロセス寿命にわたり再利用）。
        var discovered = new PluginLoader(logger.Emit).DiscoverTypes(pluginDir);
        // 起動時に Tools 検証＋ Init を行い、有効なプラグイン型のみ発火対象として残す。
        _pluginTypes = ValidateAndInit(discovered);
    }

    public int PluginCount => _pluginTypes.Count;

    /// <summary>
    /// 型ごとに1インスタンスを生成し、Tools を <see cref="ToolCatalog.ValidateTools"/> で検証。
    /// 検証 OK の型のみ <see cref="PluginBase.Init"/> を1度だけ実行・ログし、発火対象として返す。
    /// 検証 NG はエラーログを出して除外する。Init は契約上「ロード直後1度」。
    /// 注: Init を実行するインスタンスとリクエスト毎の Action インスタンスは別（隔離維持・ステートレス前提）。
    /// </summary>
    private IReadOnlyList<Type> ValidateAndInit(IReadOnlyList<Type> types)
    {
        var valid = new List<Type>();
        foreach (var type in types)
        {
            PluginBase plugin;
            try
            {
                plugin = (PluginBase)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                _logger.Emit(LogEntry.Error($"インスタンス生成失敗 ({type.FullName}): {ex.Message}"));
                continue;
            }

            var name = plugin.PluginName;
            var toolsValidation = ToolCatalog.ValidateTools(plugin.Tools);
            var eventsValidation = EventCatalog.ValidateEvents(plugin.Events);
            if (!toolsValidation.IsValid || !eventsValidation.IsValid)
            {
                foreach (var error in toolsValidation.Errors.Concat(eventsValidation.Errors))
                {
                    _logger.Emit(LogEntry.Error($"フィルタ検証失敗のため無効化: {error}") with { Source = name });
                }
                continue; // 発火対象から除外
            }
            if (plugin.Tools is null && plugin.Events is null)
            {
                // 発火条件が無い＝一切発火しない。除外はしないが警告する。
                _logger.Emit(LogEntry.Warning(
                    "Tools/Events が両方 null。発火条件が無いためこのプラグインは一切発火しない。") with { Source = name });
            }

            // 設定ロード（ConfigName 必須）。未設定/不在は無効化（発火対象から除外）。
            try
            {
                plugin.LoadConfig();
            }
            catch (Exception ex)
            {
                _logger.Emit(LogEntry.Error($"設定ロード失敗のため無効化: {ex.Message}") with { Source = name });
                continue;
            }

            try
            {
                foreach (var entry in plugin.Init())
                {
                    _logger.Emit(entry with { Source = name });
                }
            }
            catch (Exception ex)
            {
                // Init 失敗はフェイルオープン（発火は継続）。
                _logger.Emit(LogEntry.Error($"Init 失敗: {ex.Message}") with { Source = name });
            }
            valid.Add(type);
        }
        return valid;
    }

    /// <summary>1 件の hook データを処理し、判定を返す。</summary>
    public Task<HostDecision> RunAsync(HookData data)
        => new PluginHost(_logger.Emit, _maxParallel).RunAsync(_pluginTypes, data);
}
