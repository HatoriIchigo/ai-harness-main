namespace ai_harness_main;

/// <summary>
/// <c>--project</c>: 稼働中の daemon がメモリに展開しているプロジェクトの一覧を表示する。
/// daemon 未起動は異常ではない（hook が来ていないだけ）ため、注記を stderr に出してヘッダのみを返す。
/// </summary>
internal static class ProjectsCommand
{
    public static async Task<int> RunAsync()
    {
        var response = await DaemonClient.TryQueryProjectsAsync().ConfigureAwait(false);
        if (response is null)
        {
            await Console.Error.WriteLineAsync(
                "daemon が起動していません（メモリ上のプロジェクトはありません）。").ConfigureAwait(false);
        }

        var roots = response?.Roots ?? [];
        var rows = roots.Select((root, i) => new[] { (i + 1).ToString(), root }).ToList();
        TextTable.Write(Console.Out, ["#", "project"], rows, firstColumnRight: true);
        return 0;
    }
}
