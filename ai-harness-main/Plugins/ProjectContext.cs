using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 1 プロジェクト分の実行コンテキスト。共有 <see cref="PluginRegistry"/> の型一覧を基に、
/// このプロジェクトの <c>common.yml</c>（有効化トグル）と各プラグイン YAML（個別設定）で
/// 検証・<see cref="PluginBase.Init"/> を済ませた有効プラグイン型を保持し、リクエストごとに発火・集約する。
///
/// 単一 daemon が複数プロジェクトをさばくため、本オブジェクトはプロジェクトルートをキーに生成・キャッシュされる。
/// 設定 YAML はホットリロード対象（<see cref="FileSystemWatcher"/> で監視し、変更時に再構築）。
/// 一定時間アクセスが無ければ daemon により回収（<see cref="Dispose"/>）される。
/// </summary>
internal sealed class ProjectContext : IDisposable
{
    private const int DebounceMs = 500;

    private readonly Action<LogEntry> _globalLog;
    private readonly PluginRegistry _registry;
    private readonly StateStore _stateStore;
    private readonly object _buildLock = new();

    // 再構築（ホットリロード）で差し替わる状態。RunAsync から読まれるため volatile。
    private volatile ProjectConfig _config;
    private volatile Logger _logger;
    private volatile IReadOnlyList<Type> _validTypes;

    private FileSystemWatcher? _watcher;
    private int _reloadGen;
    private long _lastAccessTicksUtc;

    private ProjectContext(
        PluginRegistry registry, Action<LogEntry> globalLog,
        ProjectConfig config, Logger logger, IReadOnlyList<Type> validTypes, StateStore stateStore)
    {
        _registry = registry;
        _globalLog = globalLog;
        _config = config;
        _logger = logger;
        _validTypes = validTypes;
        _stateStore = stateStore;
        Touch();
    }

    /// <summary>このコンテキストのプロジェクトルート。</summary>
    public string ProjectRoot => _config.ProjectRoot;

    /// <summary>最終アクセス（UTC）。回収判定に使う。</summary>
    public DateTime LastAccessUtc => new(Interlocked.Read(ref _lastAccessTicksUtc), DateTimeKind.Utc);

    /// <summary>
    /// プロジェクトルートから設定をロードし、有効プラグイン型を検証・Init してコンテキストを構築する。
    /// 設定 YAML の監視（ホットリロード）も開始する。
    /// </summary>
    public static ProjectContext Create(
        PluginRegistry registry, Action<LogEntry> globalLog, string projectRoot)
    {
        var config = ProjectConfig.Load(projectRoot, out var warning);
        var logger = new Logger(config.MinLogLevel, config.LogDir, toStderr: false);
        if (warning is not null)
        {
            logger.Write(LogLevel.Warning, warning);
        }

        var validTypes = ValidateAndInit(registry.Types, config, logger);
        // state ストアは設定ホットリロード（有効プラグイン再構築）とは独立に 1 つ保持する。
        var stateStore = StateStore.Create(projectRoot, globalLog);

        // フェーズ定義（phase.yml）を用意し、state に現在フェーズが無ければ初期フェーズ（先頭定義）を設定する。
        // config 監視の開始前に行うため、この phase.yml/state.json 書き込みはホットリロードを誘発しない。
        PhaseGraph.EnsureDefault(config.ConfigDir, globalLog);
        if (stateStore.GetPhase() is null && PhaseGraph.Load(config.ConfigDir).Initial is { } initial)
        {
            stateStore.SetPhase(initial);
            globalLog(LogEntry.Info($"初期フェーズを設定: {initial}"));
        }

        var ctx = new ProjectContext(registry, globalLog, config, logger, validTypes, stateStore);
        logger.Write(LogLevel.Info,
            $"プロジェクト初期化 root={projectRoot} 有効プラグイン={validTypes.Count} parallel={config.MaxParallel}");
        ctx.StartWatching();
        return ctx;
    }

    /// <summary>1 件の hook データを処理し、判定を返す。アクセス時刻を更新する。</summary>
    public async Task<HostDecision> RunAsync(HookData data, CancellationToken ct = default)
    {
        Touch();
        var config = _config;
        var logger = _logger;
        var types = _validTypes;

        // state 全体を読み取り用に注入（発火時点のスナップショット。共有参照ゆえプラグインは書き換えない）。
        data.State = _stateStore.Current;

        // フェーズ制御コマンド（UserPromptSubmit の /harness-next-phase[-help]）は main が直接処理し、
        // プラグインは発火させない。結果は非ブロックの additionalContext で返す。
        if (data.Event == HookEvent.UserPromptSubmit && PhaseController.IsCommand(data.Prompt))
        {
            return PhaseController.Handle(config.ConfigDir, _stateStore, data.Prompt!, logger.Emit);
        }

        var outcome = await new PluginHost(logger.Emit, config.MaxParallel, config.ConfigDir)
            .RunAsync(types, data, ct).ConfigureAwait(false);

        // 各プラグインが返した state スライスを名前空間ごとに反映（差分があれば書き戻す）。
        _stateStore.ApplyAndSave(outcome.StateUpdates);

        return outcome.Decision;
    }

    private void Touch() => Interlocked.Exchange(ref _lastAccessTicksUtc, DateTime.UtcNow.Ticks);

    // ---- 検証・初期化 ----

    /// <summary>
    /// 共有型一覧から、このプロジェクトの <c>common.yml</c> の <c>tools</c> で有効化されたもののみを対象に
    /// Tools/Events を検証し、設定ロード・<see cref="PluginBase.Init"/> を 1 度実行して発火対象に残す。
    /// 単一 daemon の耐性のため、設定 <c>tools</c> に書かれたが lib に無い PluginName は<b>当該プロジェクトのみ</b>
    /// エラーログを出して除外する（全 daemon を落とさない）。
    /// </summary>
    private static IReadOnlyList<Type> ValidateAndInit(
        IReadOnlyList<Type> types, ProjectConfig config, Logger logger)
    {
        var toolToggles = config.ToolToggles;
        var valid = new List<Type>();
        var discoveredNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in types)
        {
            PluginBase plugin;
            try
            {
                plugin = (PluginBase)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                logger.Emit(LogEntry.Error($"インスタンス生成失敗 ({type.FullName}): {ex.Message}"));
                continue;
            }

            var name = plugin.PluginName;
            discoveredNames.Add(name);

            // on/off 判定: tools で true のもののみ有効。未記載・false は無効（除外）。
            if (!toolToggles.TryGetValue(name, out var enabled) || !enabled)
            {
                logger.Emit(LogEntry.Debug("無効（tools で有効化されていない）") with { Source = name });
                continue;
            }

            var toolsValidation = ToolCatalog.ValidateTools(plugin.Tools);
            var eventsValidation = EventCatalog.ValidateEvents(plugin.Events);
            if (!toolsValidation.IsValid || !eventsValidation.IsValid)
            {
                foreach (var error in toolsValidation.Errors.Concat(eventsValidation.Errors))
                {
                    logger.Emit(LogEntry.Error($"フィルタ検証失敗のため無効化: {error}") with { Source = name });
                }
                continue;
            }
            if (plugin.Tools is null && plugin.Events is null
                && plugin.FileNames is null && plugin.BashCommands is null)
            {
                logger.Emit(LogEntry.Warning(
                    "Tools/Events/FileNames/BashCommands が全て null。発火条件が無いためこのプラグインは一切発火しない。") with { Source = name });
            }

            // 設定ロード（ConfigName 必須・プロジェクトの config ディレクトリから）。失敗は無効化。
            try
            {
                plugin.LoadConfig(config.ConfigDir);
            }
            catch (Exception ex)
            {
                logger.Emit(LogEntry.Error($"設定ロード失敗のため無効化: {ex.Message}") with { Source = name });
                continue;
            }

            try
            {
                foreach (var entry in plugin.Init())
                {
                    logger.Emit(entry with { Source = name });
                }
            }
            catch (Exception ex)
            {
                // Init 失敗はフェイルオープン（発火は継続）。
                logger.Emit(LogEntry.Error($"Init 失敗: {ex.Message}") with { Source = name });
            }

            logger.Emit(LogEntry.Info("起動しました") with { Source = name });
            valid.Add(type);
        }

        // tools に書かれているが lib に見つからない PluginName はエラーログ（当該プロジェクトのみ除外）。
        var missing = toolToggles.Keys.Where(k => !discoveredNames.Contains(k)).ToList();
        if (missing.Count > 0)
        {
            logger.Emit(LogEntry.Error(
                $"common.yml の tools に指定されたが lib に存在しないプラグイン（無視）: {string.Join(", ", missing)}"));
        }

        return valid;
    }

    // ---- ホットリロード ----

    private void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(_config.ConfigDir);
            var watcher = new FileSystemWatcher(_config.ConfigDir)
            {
                Filter = "*.yml",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnConfigChanged;
            watcher.Created += OnConfigChanged;
            watcher.Deleted += OnConfigChanged;
            watcher.Renamed += OnConfigChanged;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            _logger.Write(LogLevel.Warning, $"設定監視の開始に失敗（ホットリロード無効）: {ex.Message}");
        }
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        // デバウンス: 連続イベントを束ね、最後の 1 回だけ再構築する。
        var token = Interlocked.Increment(ref _reloadGen);
        _ = Task.Run(async () =>
        {
            await Task.Delay(DebounceMs).ConfigureAwait(false);
            if (Volatile.Read(ref _reloadGen) != token)
            {
                return; // 後続イベントに追い越された
            }
            Reload();
        });
    }

    /// <summary>全 YAML（common.yml ＋ 各プラグイン設定）の変更を反映し、有効プラグイン集合を再構築する。</summary>
    private void Reload()
    {
        lock (_buildLock)
        {
            try
            {
                var config = ProjectConfig.Load(ProjectRoot, out var warning);
                var logger = new Logger(config.MinLogLevel, config.LogDir, toStderr: false);
                if (warning is not null)
                {
                    logger.Write(LogLevel.Warning, warning);
                }

                var validTypes = ValidateAndInit(_registry.Types, config, logger);

                // 原子的差し替え。実行中リクエストは差し替え前の参照を使い続ける。
                _config = config;
                _logger = logger;
                _validTypes = validTypes;

                logger.Write(LogLevel.Info, $"設定リロード完了 有効プラグイン={validTypes.Count}");
            }
            catch (Exception ex)
            {
                _globalLog(LogEntry.Error($"設定リロード失敗（従前の設定を継続）root={ProjectRoot}: {ex.Message}"));
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_watcher is { } w)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
        }
        catch
        {
            // 破棄失敗は無視。
        }
        _watcher = null;
        _stateStore.Dispose();
    }
}
