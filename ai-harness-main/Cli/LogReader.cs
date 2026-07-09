using System.Globalization;
using System.Text.Json;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// ログディレクトリ（<c>&lt;yyyy-MM-dd&gt;.jsonl</c> が日付ごとに並ぶ）を読み、新しい順に返す。
/// daemon が同じファイルへ追記中でも読めるよう共有指定で開き、壊れた行は黙って捨てる
/// （表示系コマンドが書き込み側を妨げない・落ちないことを優先する）。
/// </summary>
internal static class LogReader
{
    /// <summary>ログディレクトリ内の全ログを新しい順（同時刻は記録順）に読む。存在しなければ空。</summary>
    public static IReadOnlyList<LogRecord> ReadNewestFirst(string logDir)
    {
        if (!Directory.Exists(logDir))
        {
            return [];
        }

        var records = new List<LogRecord>();
        foreach (var file in Directory.EnumerateFiles(logDir, "*.jsonl", SearchOption.TopDirectoryOnly).Order())
        {
            foreach (var line in ReadLines(file))
            {
                if (TryParseLine(line, out var record))
                {
                    records.Add(record);
                }
            }
        }
        // OrderByDescending は安定ソート。同一タイムスタンプの行はファイル内の記録順を保つ。
        return records.OrderByDescending(r => r.Timestamp).ToList();
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        StreamReader reader;
        try
        {
            // FileShare.ReadWrite: Logger が追記中でもロック競合で失敗しない。
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            reader = new StreamReader(stream);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        using (reader)
        {
            while (reader.ReadLine() is { } line)
            {
                yield return line;
            }
        }
    }

    private static bool TryParseLine(string line, out LogRecord record)
    {
        record = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("timestamp", out var ts)
                || !DateTime.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var timestamp))
            {
                return false;
            }
            if (!root.TryGetProperty("level", out var lv)
                || !Enum.TryParse<LogLevel>(lv.GetString(), ignoreCase: true, out var level))
            {
                return false;
            }

            var source = root.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

            // 構造化フィールドは deny の監査レコードにのみ在る。古い行には無いので既定値へ倒す。
            var isDeny = root.TryGetProperty("event", out var e) && e.GetString() == "deny";
            var kind = isDeny && root.TryGetProperty("kind", out var k) ? k.GetString() : null;

            record = new LogRecord(timestamp, level, source, message, isDeny, kind);
            return true;
        }
        catch (JsonException)
        {
            return false; // 追記途中の欠けた行など
        }
    }
}
