using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// <c>--plugin [プロジェクト]</c>:
/// プロジェクト無指定なら <c>lib/</c> にインストール済みのプラグイン一覧、
/// 指定ありならそのプロジェクトの <c>common.yml</c> による有効/無効を表示する。
///
/// daemon には問い合わせない（<c>lib/</c> と <c>common.yml</c> はディスクが真実源であり、
/// 照会のために daemon を起こしたくないため）。
/// </summary>
internal static class PluginsCommand
{
    /// <summary><see cref="PluginBase.Description"/> を書いていないプラグインの表示。</summary>
    private const string NoDescription = "-";

    public static int Run(CliOptions options)
    {
        var plugins = DiscoverPlugins();

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

        var enabledRows = plugins
            .Select((p, i) => new[]
            {
                (i + 1).ToString(),
                p.Name,
                IsEnabled(config, p.Name) ? "true" : "false",
            })
            .ToList();
        TextTable.Write(Console.Out, ["#", "name", "enabled"], enabledRows, firstColumnRight: true);
        return 0;
    }

    /// <summary>lib から発見した 1 プラグインの表示情報。</summary>
    private readonly record struct PluginInfo(string Name, string Description);

    /// <summary><c>tools</c> に <c>true</c> で書かれたもののみ有効（未記載・false は無効）。</summary>
    private static bool IsEnabled(ProjectConfig config, string pluginName) =>
        config.ToolToggles.TryGetValue(pluginName, out var enabled) && enabled;

    /// <summary><c>lib/</c> の DLL から発見したプラグイン（PluginName の辞書順）。</summary>
    private static IReadOnlyList<PluginInfo> DiscoverPlugins()
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
        return plugins;
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
