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

    /// <summary>既定コンテキストで読むべき共有アセンブリ名（型同一性を保つ対象）。</summary>
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai-harness-baselib",
    };

    public PluginLoadContext(string pluginPath)
        : base(name: $"plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 共有アセンブリは既定コンテキストに委ねる（同一型を保つ）。
        if (assemblyName.Name is { } name && SharedAssemblies.Contains(name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
