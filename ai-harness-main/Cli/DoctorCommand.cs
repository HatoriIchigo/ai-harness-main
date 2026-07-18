namespace ai_harness_main;

/// <summary>
/// <c>--doctor</c>: この配置でハーネスが機能するかを一通り確かめる。
///
/// tree-sitter の native 解決や <c>lib/</c> の配置ミスは、症状が「AST 解析に失敗してフェイルクローズ」
/// のように遠回りに出る。原因をその場で突き止められるようにする。
/// 読み取り専用で daemon を起こさない。終了コードは 0=致命的な問題なし / 1=error あり。
/// </summary>
internal static class DoctorCommand
{
    public static async Task<int> RunAsync()
    {
        Console.Out.WriteLine($"ai-harness-main {VersionCommand.Version()}");
        Console.Out.WriteLine($"path: {Environment.ProcessPath ?? "(unknown)"}");
        Console.Out.WriteLine();

        var checks = new List<DoctorCheck>
        {
            DoctorProbes.Lib(out _),
            DoctorProbes.Native(),
            DoctorProbes.Resources(),
            DoctorProbes.GlobalLogDir(),
            await DoctorProbes.DaemonAsync().ConfigureAwait(false),
            DoctorProbes.ExternalTool("git"),
            DoctorProbes.ExternalTool("dotnet"),
            // LSP 連携（common.yml の lsp:）が要求するランタイム。無くても本体・他の言語は動くため warn。
            DoctorProbes.LspRuntime("node", "python(pyright)/typescript"),
            DoctorProbes.LspRuntime("npm", "python(pyright)/typescript のインストール"),
            DoctorProbes.LspRuntime(OperatingSystem.IsWindows() ? "python" : "python3", "python(pylsp/jedi)"),
            DoctorProbes.LspRuntime("go", "go(gopls) のインストール"),
            DoctorProbes.LspRuntime("java", "java(jdtls)"),
            DoctorProbes.LspRuntime("dotnet", "csharp(csharp-ls) のインストール"),
        };

        var rows = checks.Select(c => new[] { c.Name, c.StatusText, c.Detail }).ToList();
        TextTable.Write(Console.Out, ["check", "status", "detail"], rows);
        Console.Out.WriteLine();

        var errors = checks.Count(c => c.Status == DoctorStatus.Error);
        var warns = checks.Count(c => c.Status == DoctorStatus.Warn);

        if (errors > 0)
        {
            Console.Out.WriteLine($"致命的な問題が {errors} 件あります（warn {warns} 件）。ハーネスは正しく機能しません。");
            return 1;
        }
        if (warns > 0)
        {
            Console.Out.WriteLine($"警告が {warns} 件あります。中核は動きますが、該当する機能は使えません。");
            return 0;
        }
        Console.Out.WriteLine("問題は見つかりませんでした。");
        return 0;
    }
}
