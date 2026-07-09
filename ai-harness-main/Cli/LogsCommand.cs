namespace ai_harness_main;

/// <summary>
/// <c>--logs [プロジェクト] [--n 件数] [--filter レベル,…]</c>:
/// プロジェクト無指定なら実行体隣の <c>logs/</c>（daemon 自身のライフサイクルログ）、
/// 指定ありならそのプロジェクトの <c>.claude/harness/logs/</c> を表示する。
///
/// 並びは<b>新しい順</b>。<c>--filter</c> で絞ってから <c>--n</c> で上位 N 件（＝直近 N 件）を切る。
/// </summary>
internal static class LogsCommand
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary><see cref="Logger"/> が source 未設定のログに入れる既定値（＝ハーネス本体）。</summary>
    private const string HarnessSource = "claude";

    public static int Run(CliOptions options)
    {
        if (!TryResolveLogDir(options.Project, out var logDir, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        IEnumerable<LogRecord> records = LogReader.ReadNewestFirst(logDir);
        if (options.DenyOnly)
        {
            records = records.Where(r => r.IsDeny);
        }
        if (options.Levels is { } levels)
        {
            records = records.Where(r => levels.Contains(r.Level));
        }
        if (options.Take is { } take)
        {
            records = records.Take(take);
        }

        var rows = records
            .Select(r => new[]
            {
                r.Timestamp.ToString(TimeFormat),
                LogLevels.Format(r.Level),
                Contents(r),
            })
            .ToList();
        TextTable.Write(Console.Out, ["time", "level", "contents"], rows);
        return 0;
    }

    /// <summary>
    /// 発生源がプラグインの場合だけ本文の前に付ける。列を増やさずに
    /// 「どのプラグインのログか」を落とさないため（ハーネス本体のログは無印）。
    /// </summary>
    private static string Contents(LogRecord record) =>
        OneLine(record.Source is "" or HarnessSource
            ? record.Message
            : $"{record.Source}: {record.Message}");

    /// <summary>
    /// 改行・タブをエスケープ表記に畳んで 1 レコード＝1 行を保つ。deny の理由のように複数行の
    /// メッセージをそのまま流すと表が崩れ、行単位で読む側（ai-harness-tui 等）が壊れるため。
    /// </summary>
    private static string OneLine(string message) => message
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static bool TryResolveLogDir(string? project, out string logDir, out string error)
    {
        error = "";
        if (project is null)
        {
            logDir = InstallPaths.GlobalLogDir;
            return true;
        }

        if (!ProjectPath.TryResolve(project, out var root, out error))
        {
            logDir = "";
            return false;
        }
        logDir = ProjectConfig.Load(root, out _).LogDir;
        return true;
    }
}
