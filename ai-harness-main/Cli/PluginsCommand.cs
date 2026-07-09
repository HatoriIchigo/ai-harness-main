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
    /// <summary>プラグインの説明はまだ契約に無いため、description 列はプレースホルダを出す。</summary>
    private const string NoDescription = "-";

    public static int Run(CliOptions options)
    {
        var names = DiscoverPluginNames();

        if (options.Project is null)
        {
            var rows = names.Select((name, i) => new[] { (i + 1).ToString(), name, NoDescription }).ToList();
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

        var enabledRows = names
            .Select((name, i) => new[]
            {
                (i + 1).ToString(),
                name,
                IsEnabled(config, name) ? "true" : "false",
            })
            .ToList();
        TextTable.Write(Console.Out, ["#", "name", "enabled"], enabledRows, firstColumnRight: true);
        return 0;
    }

    /// <summary><c>tools</c> に <c>true</c> で書かれたもののみ有効（未記載・false は無効）。</summary>
    private static bool IsEnabled(ProjectConfig config, string pluginName) =>
        config.ToolToggles.TryGetValue(pluginName, out var enabled) && enabled;

    /// <summary><c>lib/</c> の DLL から発見したプラグインの PluginName（辞書順）。</summary>
    private static IReadOnlyList<string> DiscoverPluginNames()
    {
        var registry = new PluginRegistry(WriteProblem, InstallPaths.LibDir);

        var names = new List<string>();
        foreach (var type in registry.Types)
        {
            try
            {
                names.Add(((PluginBase)Activator.CreateInstance(type)!).PluginName);
            }
            catch (Exception ex)
            {
                WriteProblem(LogEntry.Error($"インスタンス生成失敗 ({type.FullName}): {ex.Message}"));
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
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
