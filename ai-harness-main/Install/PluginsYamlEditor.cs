using System.Text.RegularExpressions;

namespace ai_harness_main;

/// <summary>
/// <c>config/plugins.yml</c> の <c>plugins</c> ブロックへ 1 エントリを追加／上書きする
/// （<c>--plugin install</c> の実体）。<see cref="CommonYamlEditor"/> と同じ方針で、
/// YamlDotNet での読み直し・書き戻しはせず<b>行単位の最小編集</b>で既存のコメント・書式を保つ。
///
/// エントリの同一性は URL 全体ではなく <see cref="PluginInstaller.RepoName"/>（URL 末尾のリポジトリ名）
/// で判定する。<c>--update &lt;プラグイン名&gt;</c> や配置先 DLL 名（<c>&lt;リポジトリ名&gt;.dll</c>）が
/// 同じ基準を使っているため、ここでも揃える。
/// </summary>
internal static class PluginsYamlEditor
{
    /// <summary>トップレベルの <c>plugins:</c> 行。インライン値（<c>[]</c> 等）を取る。</summary>
    private static readonly Regex PluginsKeyPattern = new(@"^plugins\s*:(?<inline>.*)$", RegexOptions.Compiled);

    /// <summary>エントリの先頭行（<c>  - path: &lt;url&gt;</c>）。</summary>
    private static readonly Regex EntryStartPattern = new(
        @"^(?<indent>\s*)-\s*path\s*:\s*(?<url>\S+)\s*(?:#.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>エントリ内の <c>branch:</c> 行。</summary>
    private static readonly Regex BranchLinePattern = new(
        @"^(?<indent>\s*)branch\s*:\s*(?<branch>\S+)\s*(?:#.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string DefaultEntryIndent = "  - ";
    private const string DefaultBranchIndent = "    ";

    /// <summary>
    /// <paramref name="filePath"/> の <c>plugins</c> に <paramref name="url"/>／<paramref name="branch"/> を
    /// 反映する。既存エントリ（リポジトリ名一致）があれば <c>path</c>／<c>branch</c> を上書き、無ければ
    /// ブロック末尾へ追記する。ファイルが無ければ <see cref="PluginsConfig"/> の既定値で <c>self</c>／
    /// <c>baselib</c> を持つ新規ファイルを作る（<paramref name="created"/> が <c>true</c>）。
    /// </summary>
    public static bool TryUpsert(
        string filePath, string url, string branch, out bool created, out string error)
    {
        created = false;
        error = "";

        try
        {
            if (!File.Exists(filePath))
            {
                CreateFromScratch(filePath, url, branch);
                created = true;
                return true;
            }

            var text = File.ReadAllText(filePath);
            var crlf = text.Contains("\r\n", StringComparison.Ordinal);
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            Upsert(lines, url, branch);

            Save(filePath, string.Join(crlf ? "\r\n" : "\n", lines));
            return true;
        }
        catch (Exception ex)
        {
            error = $"plugins.yml の更新に失敗: {ex.Message}";
            return false;
        }
    }

    /// <summary>ファイルが無いときの新規作成。<c>self</c>／<c>baselib</c> は既定値、<c>plugins</c> は今回の 1 件のみ。</summary>
    private static void CreateFromScratch(string filePath, string url, string branch)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var text =
            $"self:{Environment.NewLine}" +
            $"  path: {PluginsConfig.DefaultSelfPath}{Environment.NewLine}" +
            $"  branch: {PluginsConfig.DefaultSelfBranch}{Environment.NewLine}" +
            $"{Environment.NewLine}" +
            $"baselib:{Environment.NewLine}" +
            $"  path: {PluginsConfig.DefaultBaselibPath}{Environment.NewLine}" +
            $"  branch: {PluginsConfig.DefaultBaselibBranch}{Environment.NewLine}" +
            $"{Environment.NewLine}" +
            $"plugins:{Environment.NewLine}" +
            $"{DefaultEntryIndent}path: {url}{Environment.NewLine}" +
            $"{DefaultBranchIndent}branch: {branch}{Environment.NewLine}";
        File.WriteAllText(filePath, text);
    }

    /// <summary><paramref name="lines"/> を直接書き換える。見つからなければ例外（呼び出し側が捕捉）。</summary>
    private static void Upsert(List<string> lines, string url, string branch)
    {
        var name = PluginInstaller.RepoName(url);
        var pluginsIndex = FindPluginsKey(lines);

        // plugins キー自体が無い。末尾にブロックごと足す。
        if (pluginsIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1); // 末尾の空要素（末尾改行）は Join で復元されるため一旦外す
            }
            if (lines.Count > 0)
            {
                lines.Add("");
            }
            lines.Add("plugins:");
            lines.Add($"{DefaultEntryIndent}path: {url}");
            lines.Add($"{DefaultBranchIndent}branch: {branch}");
            lines.Add(""); // 末尾改行
            return;
        }

        var inline = PluginsKeyPattern.Match(lines[pluginsIndex]).Groups["inline"].Value.Trim();
        var isEmptyFlow = inline is "[]";
        if (inline.Length > 0 && !isEmptyFlow && !inline.StartsWith('#'))
        {
            throw new InvalidOperationException(
                $"plugins がフロー形式（{lines[pluginsIndex].Trim()}）のため自動編集できません。"
                + " ブロック形式（- path: <url>）に書き換えてから再実行してください。");
        }
        if (isEmptyFlow)
        {
            lines[pluginsIndex] = "plugins:";
        }

        // plugins ブロックの範囲（次のトップレベルキー、または EOF まで）。
        var blockEnd = lines.Count;
        for (var i = pluginsIndex + 1; i < lines.Count; i++)
        {
            if (IsTopLevelKey(lines[i]))
            {
                blockEnd = i;
                break;
            }
        }

        // 既存エントリ（リポジトリ名一致）を探し、見つかれば path／branch を上書きして終える。
        for (var i = pluginsIndex + 1; i < blockEnd; i++)
        {
            var entryMatch = EntryStartPattern.Match(lines[i]);
            if (!entryMatch.Success)
            {
                continue;
            }
            if (!string.Equals(
                    PluginInstaller.RepoName(entryMatch.Groups["url"].Value), name,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines[i] = $"{entryMatch.Groups["indent"].Value}- path: {url}";

            var entryEnd = blockEnd;
            for (var j = i + 1; j < blockEnd; j++)
            {
                if (EntryStartPattern.IsMatch(lines[j]))
                {
                    entryEnd = j;
                    break;
                }
            }

            var branchLine = -1;
            for (var j = i + 1; j < entryEnd; j++)
            {
                if (BranchLinePattern.IsMatch(lines[j]))
                {
                    branchLine = j;
                    break;
                }
            }

            if (branchLine >= 0)
            {
                var branchMatch = BranchLinePattern.Match(lines[branchLine]);
                lines[branchLine] = $"{branchMatch.Groups["indent"].Value}branch: {branch}";
            }
            else
            {
                lines.Insert(i + 1, $"{DefaultBranchIndent}branch: {branch}");
            }
            return;
        }

        // 一致するエントリが無い。ブロック末尾に追記する。ファイル末尾の空行はブロックの終端判定
        // （非トップレベル行）に含まれてしまうため、追記位置からは読み飛ばす（空行の手前に挿す）。
        var insertAt = blockEnd;
        while (insertAt > pluginsIndex + 1 && lines[insertAt - 1].Length == 0)
        {
            insertAt--;
        }
        lines.Insert(insertAt, $"{DefaultEntryIndent}path: {url}");
        lines.Insert(insertAt + 1, $"{DefaultBranchIndent}branch: {branch}");
    }

    /// <summary>トップレベル（インデント無し・非コメント）の <c>plugins:</c> 行を探す。無ければ -1。</summary>
    private static int FindPluginsKey(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsTopLevelKey(lines[i]) && PluginsKeyPattern.IsMatch(lines[i]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>インデントもコメントも無い行＝トップレベルのキー行（ブロックの終端判定に使う）。</summary>
    private static bool IsTopLevelKey(string line) =>
        line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#');

    /// <summary>一時ファイル経由の atomic 置換。</summary>
    private static void Save(string filePath, string text)
    {
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, filePath, overwrite: true);
    }
}
