using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// <c>--validate [プロジェクト]</c>: そのプロジェクトの設定で hook が通る状態かを、hook を待たずに確かめる。
///
/// フェイルクローズ設計では設定が壊れた瞬間にそのプロジェクトの全 hook がブロックされる。
/// 事故ってから気づくのではなく、設定を書いた直後や commit hook／CI から確かめられるようにする。
/// 判定は daemon と同じ経路（<see cref="ProjectContext.ValidateAndInit"/>）を通すので、
/// ここで通れば hook も通る。
///
/// プロジェクト無指定なら cwd から解決する（bridge と同じ探索）。
/// 終了コードは 0=検証成功 / 1=失敗。daemon には触れず、ログも書かない。
/// </summary>
internal static class ValidateCommand
{
    public static int Run(CliOptions options)
    {
        var root = options.Project is { } specified
            ? ResolveOrNull(specified)
            : ProjectLocator.Resolve(Environment.CurrentDirectory);
        if (root is null)
        {
            return 1;
        }

        var config = ProjectConfig.Load(root, out _);
        Console.Out.WriteLine($"project: {root}");
        // HarnessSubdir は "/" 区切りのため Combine すると区切りが混ざる。表示だけ正規化する。
        Console.Out.WriteLine($"config:  {Path.GetFullPath(config.ConfigDir)}");
        Console.Out.WriteLine();

        // common.yml が「在るのに壊れている」場合、何を強制すべきか判断できない（hook は全 deny）。
        if (config.LoadError is { } loadError)
        {
            Console.Out.WriteLine($"検証に失敗しました: {ProjectConfig.ConfigFileName} を読み込めません。");
            Console.Out.WriteLine($"- {loadError}");
            return 1;
        }

        if (!File.Exists(config.ConfigFilePath))
        {
            // 設定ファイルが無いプロジェクトはハーネス対象外。hook は素通りする（deny しない）。
            Console.Out.WriteLine($"{ProjectConfig.ConfigFileName} がありません。このプロジェクトはハーネス対象外です（hook は素通り）。");
            return 0;
        }

        // 検証はプロジェクトのログを汚さない。発見時の警告・エラーだけ stderr へ出す。
        var registry = new PluginRegistry(WriteProblem, InstallPaths.LibDir);
        var validation = ProjectContext.ValidateAndInit(registry.Types, config, _ => { });

        var enabled = config.ToolToggles.Where(kv => kv.Value).Select(kv => kv.Key).Order().ToList();
        Console.Out.WriteLine($"有効化: {enabled.Count} 件 / lib の発見: {registry.Count} 件");
        foreach (var name in enabled)
        {
            Console.Out.WriteLine($"  - {name}");
        }
        Console.Out.WriteLine();

        if (!validation.IsFailClosed)
        {
            Console.Out.WriteLine($"検証に成功しました（発火対象 {validation.ValidTypes.Count} 件）。");
            return 0;
        }

        Console.Out.WriteLine($"検証に失敗しました（{validation.Errors.Count} 件）。この設定では hook が全て deny されます:");
        foreach (var error in validation.Errors)
        {
            Console.Out.WriteLine($"- {error}");
        }
        return 1;
    }

    private static string? ResolveOrNull(string project)
    {
        if (ProjectPath.TryResolve(project, out var root, out var error))
        {
            return root;
        }
        Console.Error.WriteLine(error);
        return null;
    }

    /// <summary>lib の走査で出た警告・エラーのみ stderr へ（検証結果そのものは stdout に残す）。</summary>
    private static void WriteProblem(LogEntry entry)
    {
        if (entry.Level >= LogLevel.Warning)
        {
            Console.Error.WriteLine($"[{LogLevels.Format(entry.Level)}] {entry.Message}");
        }
    }
}
