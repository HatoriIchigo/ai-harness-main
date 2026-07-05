using System.Reflection;
using System.Runtime.Loader;

namespace ai_harness_main;

/// <summary>
/// プラグイン DLL 用の <see cref="AssemblyLoadContext"/>。
/// プラグイン固有の依存は <see cref="AssemblyDependencyResolver"/> で解決する一方、
/// baselib など共有アセンブリは <c>null</c> を返して既定コンテキストへフォールバックさせる。
/// これにより main と プラグインが同一の <c>PluginBase</c> 型を共有し、キャストが成立する。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>プラグイン DLL のあるディレクトリ（＝<c>lib/</c>）。同居する管理依存の直接プローブ先。</summary>
    private readonly string _pluginDir;

    /// <summary>既定コンテキストで読むべき共有アセンブリ名（型同一性を保つ対象）。</summary>
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai-harness-baselib",
    };

    public PluginLoadContext(string pluginPath)
        : base(name: $"plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
        _pluginDir = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 共有アセンブリは既定コンテキストに委ねる（同一型を保つ）。
        if (assemblyName.Name is not { } name || SharedAssemblies.Contains(name))
        {
            return null;
        }

        // まず .deps.json ベースで解決。
        var path = _resolver.ResolveAssemblyToPath(assemblyName);

        // フォールバック: .deps.json が無くても、lib に同居する管理依存（例: TreeSitter.dll）を解決できるよう
        // プラグインと同じディレクトリ（lib）を直接プローブする。これによりプラグイン配布物を「管理 DLL のみ」に
        // 保てる（deps.json を同梱しなくてよい）。フレームワーク assembly は lib に無いので null のまま既定へ委ねる。
        if (path is null)
        {
            var candidate = Path.Combine(_pluginDir, name + ".dll");
            if (File.Exists(candidate))
            {
                path = candidate;
            }
        }

        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
