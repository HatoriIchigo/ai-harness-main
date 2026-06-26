using System.Reflection;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 指定フォルダ内の DLL から <see cref="PluginBase"/> 派生型を発見するローダ。
/// アセンブリのロード・走査（最も重い部分）は1回だけ行い、型一覧を返す。
/// インスタンス生成は呼び出し側がリクエスト毎に行う（隔離維持・モデル b）。
/// </summary>
internal sealed class PluginLoader
{
    private readonly Action<LogEntry> _log;

    public PluginLoader(Action<LogEntry> log) => _log = log;

    /// <summary>
    /// <paramref name="pluginDir"/> 内の全 *.dll を走査し、具象 <see cref="PluginBase"/> 派生型を返す。
    /// アセンブリは <see cref="PluginLoadContext"/> でロードされ、プロセス寿命の間ウォームに保たれる。
    /// </summary>
    public IReadOnlyList<Type> DiscoverTypes(string pluginDir)
    {
        if (!Directory.Exists(pluginDir))
        {
            _log(LogEntry.Warning($"プラグインフォルダが存在しない: {pluginDir}"));
            return Array.Empty<Type>();
        }

        var types = new List<Type>();

        foreach (var dll in Directory.EnumerateFiles(pluginDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            // baselib 自身が同居していてもスキップ（共有アセンブリ）。
            if (string.Equals(Path.GetFileNameWithoutExtension(dll), "ai-harness-baselib",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadFromAssemblyPath(Path.GetFullPath(dll));
                types.AddRange(ConcretePluginTypes(asm));
            }
            catch (Exception ex)
            {
                _log(LogEntry.Error($"DLL ロード失敗 ({Path.GetFileName(dll)}): {ex.Message}"));
            }
        }

        _log(LogEntry.Debug($"プラグイン型 {types.Count} 個を発見"));
        return types;
    }

    private static IEnumerable<Type> ConcretePluginTypes(Assembly asm)
    {
        IEnumerable<Type?> types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types; // 解決できた型のみ後段で採用
        }

        return types.Where(t => t is not null && !t.IsAbstract && typeof(PluginBase).IsAssignableFrom(t))
                    .Select(t => t!);
    }
}
