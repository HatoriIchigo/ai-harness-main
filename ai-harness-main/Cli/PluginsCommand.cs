using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// <c>--plugin [プロジェクト] [--enable 名,…] [--disable 名,…]</c>:
///
/// <list type="bullet">
///   <item>トグル無指定・プロジェクト無指定 … <c>lib/</c> にインストール済みのプラグイン一覧。</item>
///   <item>トグル無指定・プロジェクト指定 … そのプロジェクトの <c>common.yml</c> による有効/無効。</item>
///   <item><c>--enable</c> / <c>--disable</c> … そのプロジェクトの <c>common.yml</c> の <c>tools</c> を書き換える
///     （プロジェクト無指定なら cwd から解決）。設定 YAML はホットリロード対象なので、書き換えれば
///     daemon の再起動なしで反映される。</item>
/// </list>
///
/// daemon には問い合わせない（<c>lib/</c> と <c>common.yml</c> はディスクが真実源であり、
/// 照会・更新のために daemon を起こしたくないため）。
/// </summary>
internal static class PluginsCommand
{
    /// <summary><see cref="PluginBase.Description"/> を書いていないプラグインの表示。</summary>
    private const string NoDescription = "-";

    public static int Run(CliOptions options)
    {
        var (registry, plugins) = Discover();

        if (options.Toggles.Count > 0)
        {
            return Toggle(options, registry, plugins);
        }

        if (options.Project is null)
        {
            var rows = plugins
                .Select((p, i) => new[]
                {
                    (i + 1).ToString(),
                    p.Name,
                    string.IsNullOrWhiteSpace(p.Description) ? NoDescription : p.Description,
                })
                .ToList();
            TextTable.Write(Console.Out, ["#", "name", "description"], rows, firstColumnRight: true);
            return 0;
        }

        if (!ProjectPath.TryResolve(options.Project, out var root, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var config = ProjectConfig.Load(root, out var warning);
        if (warning is not null)
        {
            Console.Error.WriteLine(warning);
        }

        WriteEnabledTable(config, plugins);
        return 0;
    }

    /// <summary>
    /// <c>--enable</c> / <c>--disable</c>: プロジェクトの <c>common.yml</c> の <c>tools</c> を書き換える。
    ///
    /// <b>有効化はプロジェクトを壊さない場合に限る</b>。有効化したプラグインが発火できる状態に到達できないと
    /// 起動検証がフェイルクローズし、そのプロジェクトの hook が<b>全て</b> deny される
    /// （<see cref="ProjectContext.ValidateAndInit"/>）。lib に無い名前を書いた場合はもちろん、lib にあっても
    /// そのプラグインの設定 YAML（<see cref="PluginBase.ConfigName"/>）がプロジェクトの config に置かれて
    /// いなければ同じくフェイルクローズする。書き込む前に適用後の状態を検証し、有効化が原因で全 deny に
    /// なるなら<b>書き込まずに拒否</b>する（「有効化したつもりが hook 全停止」を作らない）。
    ///
    /// 無効化は状態を改善する方向なので、lib に無い名前でも・既に壊れた設定でも許す（掃除・復旧に要る）。
    ///
    /// <c>common.yml</c> が「在るのに壊れている」場合は編集しない。壊れた YAML への行編集は結果を予測できず、
    /// そのプロジェクトは既に全 deny 状態のため、まず設定を直させる。
    /// </summary>
    private static int Toggle(
        CliOptions options, PluginRegistry registry, IReadOnlyList<PluginInfo> plugins)
    {
        if (!ProjectPath.TryResolveOrLocate(options.Project, out var root, out var resolveError))
        {
            Console.Error.WriteLine(resolveError);
            return 1;
        }

        var known = plugins.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = options.Toggles
            .Where(t => t.Enable && !known.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"lib に存在しないプラグインは有効化できません: {string.Join(", ", unknown)}");
            Console.Error.WriteLine("インストール済みの一覧は ai-harness-main --plugin で確認してください。");
            return 1;
        }

        var config = ProjectConfig.Load(root, out var warning);
        if (warning is not null)
        {
            Console.Error.WriteLine(warning);
            Console.Error.WriteLine(
                $"{ProjectConfig.ConfigFileName} を直してから再実行してください（この状態では hook が全て deny されます）。");
            return 1;
        }

        if (!CanEnable(options.Toggles, config, registry, out var blockers))
        {
            Console.Error.WriteLine("有効化を中止しました（この設定では hook が全て deny されます）:");
            foreach (var blocker in blockers)
            {
                Console.Error.WriteLine($"- {blocker}");
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"プラグインの設定 YAML を {Path.GetFullPath(config.ConfigDir)} に置いてから再実行してください。");
            return 1;
        }

        if (!CommonYamlEditor.TryApply(
                config.ConfigFilePath, options.Toggles, out var results, out var created, out var editError))
        {
            Console.Error.WriteLine(editError);
            return 1;
        }

        Console.Out.WriteLine($"project: {root}");
        // HarnessSubdir は "/" 区切りのため Combine すると区切りが混ざる。表示だけ正規化する。
        Console.Out.WriteLine(
            $"config:  {Path.GetFullPath(config.ConfigFilePath)}{(created ? "（新規作成）" : "")}");
        Console.Out.WriteLine();

        foreach (var result in results)
        {
            Console.Out.WriteLine($"  {Describe(result.Outcome)}: {result.PluginName}");
        }
        Console.Out.WriteLine();

        // 書き換え後の状態を読み直して表示する（この表が daemon の見るものと一致する）。
        WriteEnabledTable(ProjectConfig.Load(root, out _), plugins);
        return 0;
    }

    /// <summary>
    /// トグル適用後の状態で起動検証を行い、<b>今回有効化するプラグインが原因で</b>フェイルクローズしないかを
    /// 確かめる。適用前から在る別プラグインのエラーは今回の操作で作ったものではないため拒否理由にしない
    /// （それは <c>--validate</c> の領分）。無効化のみの操作は状態を悪化させないので検証しない。
    /// </summary>
    private static bool CanEnable(
        IReadOnlyList<(string Name, bool Enable)> toggles,
        ProjectConfig config,
        PluginRegistry registry,
        out IReadOnlyList<string> blockers)
    {
        blockers = [];
        var enabling = toggles.Where(t => t.Enable).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (enabling.Count == 0)
        {
            return true;
        }

        var merged = new Dictionary<string, bool>(config.ToolToggles, StringComparer.Ordinal);
        foreach (var (name, enable) in toggles)
        {
            merged[name] = enable;
        }
        var simulated = new ProjectConfig
        {
            ProjectRoot = config.ProjectRoot,
            ConfigDir = config.ConfigDir,
            LogDir = config.LogDir,
            MaxParallel = config.MaxParallel,
            MinLogLevel = config.MinLogLevel,
            ToolToggles = merged,
        };

        // 検証はプロジェクトのログを汚さない（daemon の経路と違い、ここではログを捨てる）。
        var validation = ProjectContext.ValidateAndInit(registry.Types, simulated, _ => { });

        // 起動検証のエラーは "<PluginName>: 理由" 形式。今回有効化した名前に起因するものだけを拾う。
        blockers = validation.Errors
            .Where(e => enabling.Any(name => e.StartsWith($"{name}:", StringComparison.Ordinal)))
            .ToList();
        return blockers.Count == 0;
    }

    /// <summary>切り替え結果の 1 行表示。</summary>
    private static string Describe(ToggleOutcome outcome) => outcome switch
    {
        ToggleOutcome.Enabled => "有効化",
        ToggleOutcome.Disabled => "無効化",
        ToggleOutcome.AlreadyEnabled => "変更なし（既に有効）",
        _ => "変更なし（既に無効）",
    };

    /// <summary>lib の全プラグインと、そのプロジェクトでの有効/無効を表にする。</summary>
    private static void WriteEnabledTable(ProjectConfig config, IReadOnlyList<PluginInfo> plugins)
    {
        var rows = plugins
            .Select((p, i) => new[]
            {
                (i + 1).ToString(),
                p.Name,
                IsEnabled(config, p.Name) ? "true" : "false",
            })
            .ToList();
        TextTable.Write(Console.Out, ["#", "name", "enabled"], rows, firstColumnRight: true);
    }

    /// <summary>lib から発見した 1 プラグインの表示情報。</summary>
    private readonly record struct PluginInfo(string Name, string Description);

    /// <summary><c>tools</c> に <c>true</c> で書かれたもののみ有効（未記載・false は無効）。</summary>
    private static bool IsEnabled(ProjectConfig config, string pluginName) =>
        config.ToolToggles.TryGetValue(pluginName, out var enabled) && enabled;

    /// <summary>
    /// <c>lib/</c> の DLL からプラグインを発見する（PluginName の辞書順）。
    /// 有効化の事前検証（<see cref="CanEnable"/>）でも同じ型一覧を使うため <see cref="PluginRegistry"/> ごと返す。
    /// </summary>
    private static (PluginRegistry Registry, IReadOnlyList<PluginInfo> Plugins) Discover()
    {
        var registry = new PluginRegistry(WriteProblem, InstallPaths.LibDir);

        var plugins = new List<PluginInfo>();
        foreach (var type in registry.Types)
        {
            try
            {
                var plugin = (PluginBase)Activator.CreateInstance(type)!;
                plugins.Add(new PluginInfo(plugin.PluginName, plugin.Description));
            }
            catch (Exception ex)
            {
                WriteProblem(LogEntry.Error($"インスタンス生成失敗 ({type.FullName}): {ex.Message}"));
            }
        }
        plugins.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return (registry, plugins);
    }

    /// <summary>発見時の警告・エラーのみ stderr へ（表そのものは stdout に残す）。</summary>
    private static void WriteProblem(LogEntry entry)
    {
        if (entry.Level >= LogLevel.Warning)
        {
            Console.Error.WriteLine($"[{LogLevels.Format(entry.Level)}] {entry.Message}");
        }
    }
}
