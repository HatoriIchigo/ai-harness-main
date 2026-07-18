using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// <c>--lsp [プロジェクト]</c>:
///
/// <list type="bullet">
///   <item>プロジェクト無指定 … <see cref="LspCatalog"/> の対応言語・候補サーバの一覧（daemon 不要）。</item>
///   <item>プロジェクト指定 … そのプロジェクトの <c>common.yml</c> の <c>lsp:</c> 宣言（ディスクの事実）と、
///     daemon に生存していれば実際の稼働状況（言語・サーバ・状態・エラー）を突き合わせて表示する。</item>
/// </list>
///
/// <c>--plugin</c> と同じく、この照会のために daemon を新規に起こしたり、プロジェクトを生成したりはしない
/// （生存していなければ「未起動」と表示するだけ）。
/// </summary>
internal static class LspCommand
{
    public static async Task<int> RunAsync(string? project)
    {
        if (project is null)
        {
            WriteCatalog();
            return 0;
        }

        if (!ProjectPath.TryResolve(project, out var root, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var config = ProjectConfig.Load(root, out var warning);
        if (warning is not null)
        {
            Console.Error.WriteLine(warning);
        }

        var response = await DaemonClient.TryQueryLspAsync(root).ConfigureAwait(false);
        WriteProjectStatus(root, config.LspLanguages, response);
        return 0;
    }

    /// <summary>
    /// stdout はテーブルのみ（1 行目が必ずヘッダ）に揃える。<c>--project</c> と同じ規約
    /// （<see cref="ProjectsCommand"/> 参照）で、TUI（<c>ai-harness-tui</c>）が <c>TableParser</c> で
    /// そのまま解析できるようにするため。説明・注記は stderr へ出す。
    /// </summary>
    private static void WriteCatalog()
    {
        var rows = LspCatalog.Languages
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => kv.Value.Servers.Keys
                .OrderBy(s => s, StringComparer.Ordinal)
                .Select(server => new[]
                {
                    kv.Key,
                    server == kv.Value.DefaultServer ? $"{server} (既定)" : server,
                }))
            .ToList();

        TextTable.Write(Console.Out, ["language", "server"], rows);
        Console.Error.WriteLine(
            "common.yml の lsp: に言語名を列挙すると有効化される。複数候補がある言語は lsp.yml の servers: で選べる。");
    }

    private static void WriteProjectStatus(
        string root, IReadOnlyList<string> declared, LspStatusResponse? response)
    {
        if (declared.Count == 0)
        {
            TextTable.Write(Console.Out, ["language", "server", "status", "error"], []);
            Console.Error.WriteLine($"project: {root}");
            Console.Error.WriteLine("common.yml の lsp: に言語が指定されていません。");
            return;
        }

        var live = response?.Languages ?? [];
        var rows = declared
            .OrderBy(l => l, StringComparer.Ordinal)
            .Select(language => live.TryGetValue(language, out var state)
                ? new[] { language, state.Server, state.Status.ToString(), state.Error ?? "" }
                : new[] { language, "-", "未起動", "" })
            .ToList();
        TextTable.Write(Console.Out, ["language", "server", "status", "error"], rows);

        Console.Error.WriteLine($"project: {root}");
        if (response is null)
        {
            Console.Error.WriteLine("daemon が起動していません（hook が来れば起動する）。");
        }
        else if (live.Count == 0)
        {
            Console.Error.WriteLine("daemon は稼働中だが、このプロジェクトはまだメモリに展開されていません（hook 未着火）。");
        }
    }
}
