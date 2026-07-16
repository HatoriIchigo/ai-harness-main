using System.Reflection;
using System.Runtime.InteropServices;

namespace ai_harness_main;

/// <summary>
/// <c>--version</c>: 稼働しているバイナリの素性を出す。
///
/// <c>--update</c> による自己更新があるため、「いま動いているのがどの版か」を確かめる手段が要る。
/// 版は csproj の <c>InformationalVersion</c>（例 <c>0.0.3α</c>）。publish 時に
/// <c>-p:SourceRevisionId=&lt;sha&gt;</c> を渡すと <c>0.0.3α+&lt;sha&gt;</c> のように追記される。
/// </summary>
internal static class VersionCommand
{
    public static int Run()
    {
        Console.Out.WriteLine($"ai-harness-main {Version()}");
        Console.Out.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription} / {RuntimeInformation.RuntimeIdentifier}");
        Console.Out.WriteLine($"path:    {Environment.ProcessPath ?? "(unknown)"}");
        return 0;
    }

    /// <summary>コミット sha は先頭からこの長さだけ見せる。</summary>
    private const int ShaLength = 7;

    /// <summary>表示用の版。属性が無ければアセンブリの数値版へ倒す。<c>--doctor</c> も使う。</summary>
    public static string Version()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (informational is { InformationalVersion.Length: > 0 })
        {
            return ShortenSha(informational.InformationalVersion);
        }
        return assembly.GetName().Version?.ToString() ?? "(unknown)";
    }

    /// <summary>
    /// <c>0.0.3α+&lt;sha&gt;</c> の sha を短縮する。SDK は git リポジトリからビルドすると
    /// 40 桁のフル sha を埋めるため、そのままでは読みにくい。
    /// </summary>
    private static string ShortenSha(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || version.Length - plus - 1 <= ShaLength)
        {
            return version;
        }
        return version[..(plus + 1 + ShaLength)];
    }
}
