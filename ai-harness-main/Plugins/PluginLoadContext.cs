using System.Reflection;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// プラグインの管理 DLL が <c>DllImport</c> するネイティブを解決する。
    ///
    /// <c>.deps.json</c> ベースの解決に失敗したら、実行体隣の <c>runtimes/&lt;rid&gt;/native/</c> を
    /// <b>直接プローブ</b>する（管理依存を <c>lib/</c> から直接プローブするのと同じ発想）。
    ///
    /// <see cref="Program.PreloadNativeLibraries"/> のフルパス事前ロードだけでは足りない。dlopen が
    /// 既ロードとして再利用する鍵は <b>SONAME</b> であり、ファイル名と一致するとは限らないため
    /// （実測: <c>libtree-sitter.so</c> の SONAME は <c>libtree-sitter.so.0.26</c>）。事前ロードしても
    /// <c>DllImport("tree-sitter")</c> からの <c>libtree-sitter.so</c> 探索は既ロードに当たらず、
    /// OS 既定探索（実行体ディレクトリを含まない）で失敗する。ここで明示的に解決する。
    /// </summary>
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName)
                   ?? FindNative(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }

    /// <summary>実行体隣の <c>runtimes/&lt;rid&gt;/native/</c> から、OS の命名規約で候補を探す。</summary>
    private static string? FindNative(string name)
    {
        var dir = Path.Combine(
            AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (var candidate in Candidates(name))
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }
        return null;
    }

    /// <summary>ベア名（<c>tree-sitter</c>）から、その OS でのファイル名候補を並べる。</summary>
    private static IEnumerable<string> Candidates(string name)
    {
        yield return name; // 既にファイル名まで書かれている場合

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return $"{name}.dll";
            yield break;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return $"lib{name}.dylib";
            yield return $"{name}.dylib";
            yield break;
        }
        yield return $"lib{name}.so";
        yield return $"{name}.so";
    }
}
