using System.Text.RegularExpressions;

namespace ai_harness_main;

/// <summary>1 プラグインに対する切り替えの結果。</summary>
internal enum ToggleOutcome
{
    /// <summary>無効から有効へ書き換えた。</summary>
    Enabled,

    /// <summary>有効から無効へ書き換えた。</summary>
    Disabled,

    /// <summary>既に有効だったため書き換えていない。</summary>
    AlreadyEnabled,

    /// <summary>既に無効（<c>false</c> または未記載）だったため書き換えていない。</summary>
    AlreadyDisabled,
}

/// <summary>1 プラグインの切り替え結果。</summary>
internal readonly record struct ToggleResult(string PluginName, ToggleOutcome Outcome);

/// <summary>
/// <c>common.yml</c> の <c>tools</c> ブロックを<b>行単位の最小編集</b>で書き換える
/// （<c>--plugin --enable</c> / <c>--disable</c> の実体）。
///
/// YamlDotNet で読み直して書き戻すとコメント・キー順・書式が失われる。<c>common.yml</c> は利用者が手で
/// 書く設定ファイルであり、既定テンプレートも解説コメントを持つため、該当行だけを差し替える。
/// 行末コメント（<c>- foo: true  # 説明</c>）も保持する。
///
/// 書き込みは一時ファイル経由の atomic 置換。<see cref="ProjectContext"/> の <see cref="FileSystemWatcher"/> が
/// これを検知し、当該プロジェクトの有効プラグイン集合を再構築する（daemon の再起動は不要）。
/// </summary>
internal static class CommonYamlEditor
{
    /// <summary>トップレベルの <c>tools:</c> 行。インライン値（<c>[]</c> 等）を取る。</summary>
    private static readonly Regex ToolsKeyPattern = new(@"^tools\s*:(?<inline>.*)$", RegexOptions.Compiled);

    /// <summary><c>tools</c> ブロックの 1 エントリ（<c>  - name: true  # コメント</c>）。</summary>
    private static readonly Regex EntryPattern = new(
        @"^(?<prefix>\s*-\s+)(?<name>[^\s:#][^:]*?)(?<sep>\s*:\s*)(?<value>true|false)(?<suffix>\s*(?:#.*)?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>エントリが 1 つも無いときに使う既定のインデント。</summary>
    private const string DefaultPrefix = "  - ";

    /// <summary>
    /// <paramref name="filePath"/> の <c>tools</c> に <paramref name="toggles"/> を反映する。
    /// ファイルが無ければ既定テンプレート（実行体隣の <c>resources/common.yml</c>）から新規作成する
    /// （<paramref name="created"/> が <c>true</c>）。
    ///
    /// 未記載のプラグインを <c>disable</c> しても行は増やさない（未記載＝無効のため書く意味がない）。
    /// 変更が 1 件も無ければファイルには一切触れない（無用なホットリロードを起こさない）。
    /// </summary>
    public static bool TryApply(
        string filePath,
        IReadOnlyList<(string Name, bool Enable)> toggles,
        out IReadOnlyList<ToggleResult> results,
        out bool created,
        out string error)
    {
        results = [];
        created = false;
        error = "";

        try
        {
            if (!File.Exists(filePath))
            {
                if (!TryCreateFromTemplate(filePath, out error))
                {
                    return false;
                }
                created = true;
            }

            var text = File.ReadAllText(filePath);
            var crlf = text.Contains("\r\n", StringComparison.Ordinal);
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

            if (!TryEdit(lines, toggles, out var edited, out var toggleResults, out error))
            {
                return false;
            }
            results = toggleResults;

            if (edited)
            {
                Save(filePath, string.Join(crlf ? "\r\n" : "\n", lines));
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ProjectConfig.ConfigFileName} の更新に失敗: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 実行体隣の <c>resources/common.yml</c> をコピーして設定ファイルを新規作成する。
    /// テンプレートが見つからない配置でも切り替えは行えるべきなので、最小の <c>tools:</c> だけを書いた
    /// ファイルへフォールバックする。
    /// </summary>
    private static bool TryCreateFromTemplate(string filePath, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var template = Path.Combine(InstallPaths.ResourcesDir, ProjectConfig.ConfigFileName);
            if (File.Exists(template))
            {
                File.Copy(template, filePath);
            }
            else
            {
                File.WriteAllText(filePath, $"tools: []{Environment.NewLine}");
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ProjectConfig.ConfigFileName} の作成に失敗: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// <paramref name="lines"/> を直接書き換える。戻り値は編集の成否（<c>false</c> は
    /// <paramref name="error"/> 付き）、<paramref name="edited"/> は実際に行が変わったか。
    /// </summary>
    private static bool TryEdit(
        List<string> lines,
        IReadOnlyList<(string Name, bool Enable)> toggles,
        out bool edited,
        out IReadOnlyList<ToggleResult> results,
        out string error)
    {
        edited = false;
        error = "";
        var toggleResults = new List<ToggleResult>();
        results = toggleResults;

        var toolsIndex = FindToolsKey(lines);

        // tools キー自体が無いファイル。有効化する分だけをブロックごと末尾に足す。
        if (toolsIndex < 0)
        {
            var added = new List<string>();
            foreach (var (name, enable) in toggles)
            {
                if (!enable)
                {
                    toggleResults.Add(new ToggleResult(name, ToggleOutcome.AlreadyDisabled));
                    continue;
                }
                added.Add($"{DefaultPrefix}{name}: true");
                toggleResults.Add(new ToggleResult(name, ToggleOutcome.Enabled));
            }
            if (added.Count == 0)
            {
                return true;
            }

            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1); // 末尾の空要素（末尾改行）は Join で復元されるため一旦外す
            }
            lines.Add("tools:");
            lines.AddRange(added);
            lines.Add(""); // 末尾改行
            edited = true;
            return true;
        }

        var inline = ToolsKeyPattern.Match(lines[toolsIndex]).Groups["inline"].Value.Trim();
        var isEmptyFlow = inline is "[]";
        if (inline.Length > 0 && !isEmptyFlow && !inline.StartsWith('#'))
        {
            // tools: [a, b] のようなフロー形式。行単位では安全に編集できない。
            error = $"tools がフロー形式（{lines[toolsIndex].Trim()}）のため自動編集できません。"
                + " ブロック形式（- <プラグイン名>: true）に書き換えてから再実行してください。";
            return false;
        }

        // tools ブロックの範囲（次のトップレベルキー、または EOF まで）。
        var blockEnd = lines.Count;
        for (var i = toolsIndex + 1; i < lines.Count; i++)
        {
            if (IsTopLevelKey(lines[i]))
            {
                blockEnd = i;
                break;
            }
        }

        // ブロック内の既存エントリ（name → 行番号）と、挿入位置・インデントの手本。
        var entryIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastEntryLine = -1;
        var prefix = DefaultPrefix;
        for (var i = toolsIndex + 1; i < blockEnd; i++)
        {
            var match = EntryPattern.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }
            entryIndex[match.Groups["name"].Value.Trim()] = i;
            lastEntryLine = i;
            prefix = match.Groups["prefix"].Value;
        }

        var insertions = new List<string>();
        foreach (var (name, enable) in toggles)
        {
            if (entryIndex.TryGetValue(name, out var line))
            {
                var match = EntryPattern.Match(lines[line]);
                var current = bool.Parse(match.Groups["value"].Value);
                if (current == enable)
                {
                    toggleResults.Add(new ToggleResult(
                        name, enable ? ToggleOutcome.AlreadyEnabled : ToggleOutcome.AlreadyDisabled));
                    continue;
                }

                // 値だけ差し替え、インデントと行末コメントは元のまま残す。
                lines[line] = string.Concat(
                    match.Groups["prefix"].Value,
                    match.Groups["name"].Value,
                    match.Groups["sep"].Value,
                    enable ? "true" : "false",
                    match.Groups["suffix"].Value);
                edited = true;
                toggleResults.Add(new ToggleResult(
                    name, enable ? ToggleOutcome.Enabled : ToggleOutcome.Disabled));
                continue;
            }

            // 未記載＝既に無効。無効化の要求なら書く必要がない。
            if (!enable)
            {
                toggleResults.Add(new ToggleResult(name, ToggleOutcome.AlreadyDisabled));
                continue;
            }
            insertions.Add($"{prefix}{name}: true");
            toggleResults.Add(new ToggleResult(name, ToggleOutcome.Enabled));
        }

        if (insertions.Count == 0)
        {
            return true;
        }

        // 空フロー（tools: []）にエントリは足せないので、ブロック形式の見出しへ直す。
        if (isEmptyFlow)
        {
            lines[toolsIndex] = ToolsKeyPattern.Replace(lines[toolsIndex], "tools:");
        }
        lines.InsertRange(lastEntryLine >= 0 ? lastEntryLine + 1 : toolsIndex + 1, insertions);
        edited = true;
        return true;
    }

    /// <summary>トップレベル（インデント無し・非コメント）の <c>tools:</c> 行を探す。無ければ -1。</summary>
    private static int FindToolsKey(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsTopLevelKey(lines[i]) && ToolsKeyPattern.IsMatch(lines[i]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>インデントもコメントも無い行＝トップレベルのキー行（ブロックの終端判定に使う）。</summary>
    private static bool IsTopLevelKey(string line) =>
        line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#');

    /// <summary>一時ファイル経由の atomic 置換（ホットリロードが半端な内容を読まないように）。</summary>
    private static void Save(string filePath, string text)
    {
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, filePath, overwrite: true);
    }
}
