using System.Reflection;
using System.Runtime.InteropServices;
using ai_harness_baselib;

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

    /// <summary>
    /// 表示用の版。読み出し規則は <see cref="PluginBase.Version"/>（各プラグイン DLL）と同一で、
    /// 実装は baselib に置く（本体とプラグインで版の見え方を揃えるため）。<c>--doctor</c> も使う。
    /// </summary>
    public static string Version() =>
        AssemblyVersionReader.Read(Assembly.GetExecutingAssembly());
}
