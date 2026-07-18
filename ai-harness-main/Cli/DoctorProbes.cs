using System.Diagnostics;
using System.Runtime.InteropServices;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// <c>--doctor</c> の個々の検査。いずれも読み取り専用で、daemon を起こさない。
/// 「失敗すると何が壊れるか」で <see cref="DoctorStatus"/> を決める。中核（プラグイン発火）が
/// 成り立たないものは error、一部機能（tree-sitter プラグイン・自己更新）だけが死ぬものは warn。
/// </summary>
internal static class DoctorProbes
{
    /// <summary>外部コマンドの応答待ち上限。</summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(5);

    /// <summary><c>lib/</c> の DLL と、そこから発見できたプラグイン型。</summary>
    public static DoctorCheck Lib(out int pluginTypeCount)
    {
        pluginTypeCount = 0;
        var dir = InstallPaths.LibDir;
        if (!Directory.Exists(dir))
        {
            return DoctorCheck.Error("lib/", $"存在しない: {dir}（プラグインが 1 つも動かない）");
        }

        var dlls = Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).Count();
        var problems = new List<string>();
        var registry = new PluginRegistry(e => AddProblem(problems, e), dir);
        pluginTypeCount = registry.Count;

        if (problems.Count > 0)
        {
            return DoctorCheck.Error("lib/", $"DLL {dlls} 個 / プラグイン型 {registry.Count} 個。{string.Join("; ", problems)}");
        }
        if (registry.Count == 0)
        {
            return DoctorCheck.Warn("lib/", $"DLL {dlls} 個だがプラグイン型が 0 個: {dir}");
        }
        return DoctorCheck.Ok("lib/", $"DLL {dlls} 個 / プラグイン型 {registry.Count} 個");
    }

    /// <summary>
    /// 実行体隣の <c>runtimes/&lt;rid&gt;/native</c> を実際にロードしてみる。
    /// tree-sitter grammar はここからフルパスで事前ロードされる（ベア名では解決できない）。
    /// 無い・ロードできない場合、tree-sitter を使うプラグインだけが AST 解析に失敗する。
    /// </summary>
    public static DoctorCheck Native()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var dir = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native");
        if (!Directory.Exists(dir))
        {
            return DoctorCheck.Warn("native (tree-sitter)",
                $"存在しない: {dir}（tree-sitter を使うプラグインは全 deny になる）");
        }

        var files = Directory.EnumerateFiles(dir).ToList();
        var failed = new List<string>();
        foreach (var file in files)
        {
            try
            {
                NativeLibrary.Load(file);
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}（{ex.GetType().Name}）");
            }
        }

        if (failed.Count > 0)
        {
            var shown = string.Join(", ", failed.Take(3));
            var rest = failed.Count > 3 ? $" ほか {failed.Count - 3} 件" : "";
            return DoctorCheck.Warn("native (tree-sitter)",
                $"{files.Count} 個中 {failed.Count} 個がロード不可: {shown}{rest}");
        }
        return DoctorCheck.Ok("native (tree-sitter)", $"{files.Count} 個をロード可 (rid={rid})");
    }

    /// <summary>プロジェクトへコピーする既定リソース（<c>phase.yml</c> 等）。</summary>
    public static DoctorCheck Resources()
    {
        var dir = InstallPaths.ResourcesDir;
        if (!Directory.Exists(dir))
        {
            return DoctorCheck.Warn("resources/", $"存在しない: {dir}（既定 phase.yml を配れない）");
        }
        var count = Directory.EnumerateFiles(dir).Count();
        return DoctorCheck.Ok("resources/", $"{count} ファイル");
    }

    /// <summary>daemon の稼働状況。未起動は異常ではない（hook が来れば bridge が起こす）。</summary>
    public static async Task<DoctorCheck> DaemonAsync()
    {
        var pipe = HarnessPipe.Name();
        var response = await DaemonClient.TryQueryProjectsAsync().ConfigureAwait(false);
        if (response is null)
        {
            return DoctorCheck.Ok("daemon", $"未起動（hook が来れば起動する） pipe={pipe}");
        }
        return DoctorCheck.Ok("daemon", $"稼働中 pipe={pipe} / メモリ上 {response.Roots.Count} プロジェクト");
    }

    /// <summary>グローバルログの出力先が作れるか。</summary>
    public static DoctorCheck GlobalLogDir()
    {
        var dir = InstallPaths.GlobalLogDir;
        try
        {
            Directory.CreateDirectory(dir);
            return DoctorCheck.Ok("logs/", dir);
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn("logs/", $"作成できない: {dir}（{ex.Message}）");
        }
    }

    /// <summary>
    /// <c>--update</c>（プラグイン更新・自己更新）が要求する外部コマンド。
    /// 無くてもハーネス本体は動くため warn。
    /// </summary>
    public static DoctorCheck ExternalTool(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return DoctorCheck.Warn(command, "起動できない（--update が使えない）");
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(ToolTimeout))
            {
                process.Kill(entireProcessTree: true);
                return DoctorCheck.Warn(command, "応答なし（--update が使えない）");
            }
            var first = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return process.ExitCode == 0
                ? DoctorCheck.Ok(command, first)
                : DoctorCheck.Warn(command, $"異常終了 exit={process.ExitCode}（--update が使えない）");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn(command, $"PATH に見つからない（--update が使えない）: {ex.Message}");
        }
    }

    /// <summary>
    /// LSP サーバが要求するランタイム（<c>node</c>／<c>npm</c>／<c>python</c>／<c>go</c>／<c>java</c>／
    /// <c>dotnet</c>）が使えるか。<paramref name="usedBy"/> は「これが無いと何が使えないか」の表示用
    /// （例: "python: pyright/typescript"）。無くてもハーネス本体・他の言語は動くため warn。
    ///
    /// <see cref="ExternalTool"/> と別メソッドにしている理由: Windows では <c>npm</c> 等が <c>.cmd</c> 経由の
    /// ことがあり、<c>UseShellExecute=false</c> の直接起動では解決できないことがある
    /// （<see cref="LspManager"/> 実装時に実測で確認済みの問題）。<c>git</c>／<c>dotnet</c> は実体 exe なので
    /// <see cref="ExternalTool"/> の直接起動のままで問題ないが、こちらは <c>cmd.exe /c</c> 経由に統一して
    /// コマンドの実体を問わず解決できるようにする。
    /// </summary>
    public static DoctorCheck LspRuntime(string command, string usedBy)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
            }
            psi.ArgumentList.Add("--version");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = Process.Start(psi);
            if (process is null)
            {
                return DoctorCheck.Warn(command, $"起動できない（{usedBy} が使えない）");
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(ToolTimeout))
            {
                process.Kill(entireProcessTree: true);
                return DoctorCheck.Warn(command, $"応答なし（{usedBy} が使えない）");
            }
            var first = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return process.ExitCode == 0
                ? DoctorCheck.Ok(command, $"{first}（{usedBy}）")
                : DoctorCheck.Warn(command, $"異常終了 exit={process.ExitCode}（{usedBy} が使えない）");
        }
        catch (Exception ex)
        {
            return DoctorCheck.Warn(command, $"PATH に見つからない（{usedBy} が使えない）: {ex.Message}");
        }
    }

    private static void AddProblem(List<string> problems, LogEntry entry)
    {
        if (entry.Level >= LogLevel.Error)
        {
            problems.Add(entry.Message);
        }
    }
}
