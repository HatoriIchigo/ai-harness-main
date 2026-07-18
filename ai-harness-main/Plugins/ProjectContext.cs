using System.Text.Json.Nodes;
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
    private volatile StartupValidation _validation;

    private FileSystemWatcher? _watcher;
    private int _reloadGen;
    private long _lastAccessTicksUtc;

    private ProjectContext(
        PluginRegistry registry, Action<LogEntry> globalLog,
        ProjectConfig config, Logger logger, StartupValidation validation, StateStore stateStore)
    {
        _registry = registry;
        _globalLog = globalLog;
        _config = config;
        _logger = logger;
        _validation = validation;
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

        // 登録（初回活性化）時のみ rule を配置する。宛先はこのプロジェクトの .claude/rules。
        // Reload / --validate は copyRulesTo を渡さない（配置しない）。
        var rulesDir = Path.Combine(projectRoot, ".claude", "rules");
        var validation = ValidateAndInit(registry.Types, config, logger.Emit, rulesDir);
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

        var ctx = new ProjectContext(registry, globalLog, config, logger, validation, stateStore);
        logger.Write(LogLevel.Info,
            $"プロジェクト初期化 root={projectRoot} 有効プラグイン={validation.ValidTypes.Count} parallel={config.MaxParallel}");
        ctx.StartWatching();
        // common.yml の lsp: に列挙された言語をバックグラウンドで起動（未起動の分のみ・hookはブロックしない）。
        LspManager.EnsureStarted(projectRoot, config.LspLanguages, logger.Emit);
        return ctx;
    }

    /// <summary>1 件の hook データを処理し、判定を返す。アクセス時刻を更新する。</summary>
    public async Task<HostDecision> RunAsync(HookData data, CancellationToken ct = default)
    {
        Touch();
        var config = _config;
        var logger = _logger;
        var validation = _validation;

        // フェイルクローズ: common.yml が「存在するのに壊れている」場合は、何を強制すべきか判断できないため通さない。
        // common.yml が無いプロジェクト（ハーネス未使用）は LoadError=null なので、この分岐に入らず素通りする。
        // common.yml を直せばホットリロードでコンテキストが再構築され、ブロックは解除される。
        if (config.LoadError is { } configError)
        {
            var reason = $"common.yml の読み込みに失敗しています（フェイルクローズ）。設定を修正してください: {configError}";
            logger.WriteDeny(new DenyEvent(
                "claude", DenyKind.FailClose, reason, data.ToolName, data.HookEventName));
            return new HostDecision(2, reason);
        }

        // フェイルクローズ: 有効化したプラグインを起動できていない＝そのガードが効かない状態では通さない。
        // 設定を直せばホットリロードでコンテキストが再構築され、ブロックは解除される。
        if (validation.IsFailClosed)
        {
            var reason = validation.Reason();
            logger.WriteDeny(new DenyEvent(
                "claude", DenyKind.FailClose, reason, data.ToolName, data.HookEventName));
            return new HostDecision(2, reason);
        }

        // state 全体を読み取り用に注入（発火時点のスナップショット。共有参照ゆえプラグインは書き換えない）。
        data.State = _stateStore.Current;
        // LSP の状態・診断キャッシュも同様に注入（プラグインが「使えるか」「何が出ているか」を判断できるように）。
        data.Lsp = LspManager.GetStatusSnapshot(ProjectRoot);
        data.LspDiagnostics = LspManager.GetDiagnosticsSnapshot(ProjectRoot);

        // 書き込み系ツールの PostToolUse では、書き込まれたファイルを対応する起動済み LSP へ同期する
        // （非ブロック。診断はサーバから非同期に届くため、この hook 自体には反映されないことが多い）。
        if (data.Event == HookEvent.PostToolUse && WriteToolNames.Contains(data.ToolName ?? "")
            && ExtractFilePath(data) is { } changedFilePath)
        {
            LspManager.NotifyFileChanged(ProjectRoot, changedFilePath);
        }

        // フェーズ制御コマンド（UserPromptSubmit の /harness-next-phase[-help]）は main が直接処理し、
        // プラグインは発火させない。結果は非ブロックの additionalContext で返す。
        if (data.Event == HookEvent.UserPromptSubmit && PhaseController.IsCommand(data.Prompt))
        {
            return PhaseController.Handle(config.ConfigDir, _stateStore, data.Prompt!, logger.Emit);
        }

        var outcome = await new PluginHost(logger.Emit, config.MaxParallel, config.ConfigDir)
            .RunAsync(validation.ValidTypes, data, ct).ConfigureAwait(false);

        // deny は監査レコードとして 1 件ずつ残す（集約後の理由文字列からは個々の由来を復元できない）。
        foreach (var deny in outcome.Denies)
        {
            logger.WriteDeny(deny);
        }

        // 各プラグインが返した state スライスを名前空間ごとに反映（差分があれば書き戻す）。
        _stateStore.ApplyAndSave(outcome.StateUpdates);

        return outcome.Decision;
    }

    /// <summary>
    /// <c>--fire</c>: 有効プラグインの <see cref="PluginBase.Fire"/> を実行し、プラグインごとの結果を返す。
    /// <paramref name="pluginName"/> 指定でその 1 つに限定（未指定は全有効プラグイン）。アクセス時刻を更新する。
    ///
    /// hook のゲートではないが、そもそも設定が壊れて有効プラグイン集合を確定できない状態
    /// （common.yml 破損・起動検証エラー）ではスキャンの前提が崩れるため、実行せず理由を返す。
    /// </summary>
    public async Task<FireReport> FireAsync(string? pluginName, CancellationToken ct = default)
    {
        Touch();
        var config = _config;
        var logger = _logger;
        var validation = _validation;

        if (config.LoadError is { } configError)
        {
            return FireReport.Failed(
                $"common.yml の読み込みに失敗しています。設定を修正してください: {configError}");
        }
        if (validation.IsFailClosed)
        {
            return FireReport.Failed(validation.Reason());
        }

        var outcomes = await new PluginHost(logger.Emit, config.MaxParallel, config.ConfigDir)
            .RunFireAsync(validation.ValidTypes, pluginName, config.ProjectRoot, ct).ConfigureAwait(false);

        if (pluginName is not null && outcomes.Count == 0)
        {
            return FireReport.Failed(
                $"プラグイン '{pluginName}' はこのプロジェクトで有効化されていません（または存在しません）。"
                + " 有効なプラグインは ai-harness-main --plugin <プロジェクト> で確認してください。");
        }
        return FireReport.Ok(outcomes);
    }

    private void Touch() => Interlocked.Exchange(ref _lastAccessTicksUtc, DateTime.UtcNow.Ticks);

    // ---- 検証・初期化 ----

    /// <summary>
    /// 共有型一覧から、このプロジェクトの <c>common.yml</c> の <c>tools</c> で有効化されたもののみを対象に
    /// Tools/Events を検証し、設定ロード・<see cref="PluginBase.Init"/> を 1 度実行して発火対象に残す。
    ///
    /// <b>フェイルクローズ</b>: 有効化されたプラグインを発火できる状態に持ち込めなかった場合、それを
    /// 除外して素通りさせるとガードが消える。理由を <see cref="StartupValidation.Errors"/> に積み、
    /// <see cref="RunAsync"/> がこのプロジェクトの hook をブロックする。
    ///
    /// インスタンス生成に失敗した型は <c>PluginName</c> を取れず、有効化されているか判定できない。
    /// 「有効かもしれないものを検証できなかった」ため、こちらも安全側（ブロック）へ倒す。
    ///
    /// ログの宛先は <paramref name="log"/>。<c>--validate</c> から呼ぶときはログを捨てられるよう
    /// <see cref="Logger"/> ではなくデリゲートを取る（検証がプロジェクトのログを汚さないため）。
    ///
    /// <paramref name="copyRulesTo"/> が非 null のとき、有効化・Init 済みの各プラグインの
    /// <see cref="PluginBase.CopyRule"/> をそのディレクトリへ実行し rule を配置する（<c>Create</c> のみ渡す。
    /// <c>Reload</c> / <c>--validate</c> は null＝配置しない）。配置は hook のゲートではないため、
    /// 失敗しても検証結果には影響させず warning ログに留める（フェイルオープン）。
    /// </summary>
    public static StartupValidation ValidateAndInit(
        IReadOnlyList<Type> types, ProjectConfig config, Action<LogEntry> log,
        string? copyRulesTo = null)
    {
        var toolToggles = config.ToolToggles;
        var valid = new List<Type>();
        var errors = new List<string>();
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
                // 名前が取れず有効/無効を判定できないため、無効化されている可能性があってもブロックする。
                log(LogEntry.Error($"インスタンス生成失敗（フェイルクローズ） ({type.FullName}): {ex.Message}"));
                errors.Add($"{type.FullName}: インスタンス生成に失敗（{ex.Message}）");
                continue;
            }

            var name = plugin.PluginName;
            discoveredNames.Add(name);

            // on/off 判定: tools で true のもののみ有効。未記載・false は無効（除外）。無効は検証しない。
            if (!toolToggles.TryGetValue(name, out var enabled) || !enabled)
            {
                log(LogEntry.Debug("無効（tools で有効化されていない）") with { Source = name });
                continue;
            }

            var toolsValidation = ToolCatalog.ValidateTools(plugin.Tools);
            var eventsValidation = EventCatalog.ValidateEvents(plugin.Events);
            if (!toolsValidation.IsValid || !eventsValidation.IsValid)
            {
                foreach (var error in toolsValidation.Errors.Concat(eventsValidation.Errors))
                {
                    log(LogEntry.Error($"フィルタ検証失敗（フェイルクローズ）: {error}") with { Source = name });
                    errors.Add($"{name}: 発火条件が不正（{error}）");
                }
                continue;
            }
            if (plugin.Tools is null && plugin.Events is null
                && plugin.FileNames is null && plugin.BashCommands is null)
            {
                // 設定では直せないプラグイン実装側の問題。発火しないだけなのでブロックはしない。
                log(LogEntry.Warning(
                    "Tools/Events/FileNames/BashCommands が全て null。発火条件が無いためこのプラグインは一切発火しない。") with { Source = name });
            }

            // 設定ロード（ConfigName 必須・プロジェクトの config ディレクトリから）。
            try
            {
                plugin.LoadConfig(config.ConfigDir);
            }
            catch (Exception ex)
            {
                log(LogEntry.Error($"設定ロード失敗（フェイルクローズ）: {ex.Message}") with { Source = name });
                errors.Add($"{name}: 設定のロードに失敗（{ex.Message}）");
                continue;
            }

            try
            {
                foreach (var entry in plugin.Init())
                {
                    log(entry with { Source = name });
                }
            }
            catch (Exception ex)
            {
                // Init を完了できないプラグインは正しく発火できるか検証できていない。
                log(LogEntry.Error($"Init 失敗（フェイルクローズ）: {ex.Message}") with { Source = name });
                errors.Add($"{name}: Init に失敗（{ex.Message}）");
                continue;
            }

            log(LogEntry.Info("起動しました") with { Source = name });

            // rule 配布（copyRulesTo は Create のときのみ非 null）。hook のゲートではないので、
            // 失敗しても検証を止めず warning に留める（フェイルオープン）。
            if (copyRulesTo is not null)
            {
                try
                {
                    if (plugin.CopyRule(copyRulesTo) is { } written)
                    {
                        log(LogEntry.Info($"rule を配置: {written}") with { Source = name });
                    }
                }
                catch (Exception ex)
                {
                    log(LogEntry.Warning($"rule 配置に失敗（継続）: {ex.Message}") with { Source = name });
                }
            }

            valid.Add(type);
        }

        // tools で有効化されたが lib に見つからない PluginName。そのガードは存在しないためブロックする。
        var missing = toolToggles.Where(kv => kv.Value && !discoveredNames.Contains(kv.Key))
                                 .Select(kv => kv.Key)
                                 .ToList();
        if (missing.Count > 0)
        {
            log(LogEntry.Error(
                $"common.yml の tools で有効化されたが lib に存在しない（フェイルクローズ）: {string.Join(", ", missing)}"));
            errors.AddRange(missing.Select(m => $"{m}: tools で有効化されているが lib に存在しない"));
        }

        return new StartupValidation(valid, errors);
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

                var validation = ValidateAndInit(_registry.Types, config, logger.Emit);

                // 原子的差し替え。実行中リクエストは差し替え前の参照を使い続ける。
                _config = config;
                _logger = logger;
                _validation = validation;

                logger.Write(LogLevel.Info,
                    $"設定リロード完了 有効プラグイン={validation.ValidTypes.Count} 起動エラー={validation.Errors.Count}");

                // lsp: が編集で増えていた場合に備え、未起動の言語があればここでも起動を試みる（冪等）。
                LspManager.EnsureStarted(ProjectRoot, config.LspLanguages, logger.Emit);
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
        LspManager.StopAll(ProjectRoot);
    }

    /// <summary>LSP への同期対象とする書き込み系ツール名。</summary>
    private static readonly HashSet<string> WriteToolNames =
        new(StringComparer.Ordinal) { "Write", "Edit", "MultiEdit", "NotebookEdit" };

    /// <summary>書き込まれたファイルパス。<c>tool_input.file_path</c> 優先、無ければ <c>notebook_path</c>、さらにトップレベル <c>file_path</c>。</summary>
    private static string? ExtractFilePath(HookData data) =>
        AsString(GetMember(data.ToolInput, "file_path"))
        ?? AsString(GetMember(data.ToolInput, "notebook_path"))
        ?? data.FilePath;

    private static JsonNode? GetMember(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var v) ? v : null;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
