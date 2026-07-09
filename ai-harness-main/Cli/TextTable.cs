namespace ai_harness_main;

/// <summary>
/// <c>ヘッダ | ヘッダ</c> 形式の等幅テーブルを書き出す。情報表示系サブコマンドの共通出力。
/// 最終列はパディングしない（メッセージに日本語が混じると文字数と表示幅が一致せず、
/// 右端を揃えようとすると逆に崩れるため）。
/// </summary>
internal static class TextTable
{
    private const string Separator = " | ";

    /// <param name="writer">出力先。</param>
    /// <param name="headers">ヘッダ行。</param>
    /// <param name="rows">各行のセル（列数は <paramref name="headers"/> と一致させる）。</param>
    /// <param name="firstColumnRight">先頭列を右詰めするか（連番列 <c>#</c> 用）。</param>
    public static void Write(
        TextWriter writer, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows,
        bool firstColumnRight = false)
    {
        var widths = new int[headers.Count];
        for (var c = 0; c < headers.Count; c++)
        {
            widths[c] = headers[c].Length;
            foreach (var row in rows)
            {
                widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        writer.WriteLine(Render(headers, widths, firstColumnRight));
        foreach (var row in rows)
        {
            writer.WriteLine(Render(row, widths, firstColumnRight));
        }
    }

    private static string Render(IReadOnlyList<string> cells, int[] widths, bool firstColumnRight)
    {
        var padded = cells.Select((cell, c) =>
            c == cells.Count - 1 ? cell
            : c == 0 && firstColumnRight ? cell.PadLeft(widths[c])
            : cell.PadRight(widths[c]));
        return string.Join(Separator, padded);
    }
}
