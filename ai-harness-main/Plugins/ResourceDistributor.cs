using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 有効プラグインが同梱する rule／skill を、そのプロジェクトの <c>.claude/rules</c>／<c>.claude/skills</c> へ
/// 配布する処理の単一実装（<see cref="PluginBase.CopyRule"/>／<see cref="PluginBase.CopySkill"/> の呼び出し元）。
///
/// <para><b>配布の契機</b>。skill は Claude Code がセッション初期化時にディスクを走査して一覧化するため、
/// ツール使用時（PreToolUse 等）に配布しても<b>その回のセッションには載らない</b>。ユーザーの入力より前に
/// ファイルが揃っている必要があるので、次の 3 つで呼ぶ。</para>
/// <list type="bullet">
///   <item><c>SessionStart</c> hook（<see cref="ProjectContext.RunAsync"/>）。SessionStart より前に走る hook は
///     無いため、実行時の配布契機はここが最も早い。プロジェクトが daemon に展開済みかどうかに関わらず毎回走る。</item>
///   <item><c>--init</c>（<see cref="InitCommand"/>）。セッションと無関係に配線と同時に配置し、
///     初回セッションから確実に載せる。</item>
///   <item>プロジェクトの初回活性化（<see cref="ProjectContext.Create"/>）。上記 2 つを通っていない
///     プロジェクトの保険。</item>
/// </list>
///
/// <para>いずれも冪等（<see cref="PluginBase.CopyRule"/>／<see cref="PluginBase.CopySkill"/> は内容が一致する
/// ファイルを書き換えない）。hook のゲートではないため、配布の失敗は呼び出し元をブロックさせず警告に留める。</para>
/// </summary>
internal static class ResourceDistributor
{
    /// <summary>配布先（プロジェクトルート基準）。</summary>
    public static string RulesDir(string projectRoot) => Path.Combine(projectRoot, ".claude", "rules");

    /// <summary>配布先（プロジェクトルート基準）。</summary>
    public static string SkillsDir(string projectRoot) => Path.Combine(projectRoot, ".claude", "skills");

    /// <summary>
    /// <paramref name="types"/> のうち <paramref name="onlyNames"/> に含まれるプラグインの rule／skill を
    /// <paramref name="projectRoot"/> 配下へ配布する。
    /// </summary>
    /// <param name="types">配布候補のプラグイン型（呼び出し元が有効なものへ絞ってから渡す）。</param>
    /// <param name="projectRoot">配布先プロジェクトのルート（絶対パス）。</param>
    /// <param name="onlyNames">
    /// 対象を <see cref="PluginBase.PluginName"/> で絞る集合。<c>null</c> なら <paramref name="types"/> 全件。
    /// </param>
    /// <param name="log">配布結果・失敗の記録先。</param>
    /// <returns>実際に書き込んだファイルの絶対パスの一覧（内容が同じで書かなかった分は含まない）。</returns>
    public static IReadOnlyList<string> Distribute(
        IReadOnlyList<Type> types, string projectRoot, ISet<string>? onlyNames, Action<LogEntry> log)
    {
        var rulesDir = RulesDir(projectRoot);
        var skillsDir = SkillsDir(projectRoot);
        var written = new List<string>();

        foreach (var type in types)
        {
            PluginBase plugin;
            try
            {
                plugin = (PluginBase)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                // 生成できない型は起動検証（ValidateAndInit）がフェイルクローズで扱う。配布側は記録だけ。
                log(LogEntry.Warning($"配布のためのインスタンス生成に失敗（継続） ({type.FullName}): {ex.Message}"));
                continue;
            }

            if (onlyNames is not null && !onlyNames.Contains(plugin.PluginName))
            {
                continue;
            }

            // rule と skill は独立に配布する（片方の失敗で他方を落とさない）。
            // 配布は Claude Code への案内文の配置であって hook のゲートではないため、失敗は警告に留める。
            Copy(() => plugin.CopyRule(rulesDir), "rule", plugin.PluginName, written, log);
            Copy(() => plugin.CopySkill(skillsDir), "skill", plugin.PluginName, written, log);
        }

        return written;
    }

    private static void Copy(
        Func<IReadOnlyList<string>> copy, string kind, string pluginName,
        List<string> written, Action<LogEntry> log)
    {
        try
        {
            foreach (var path in copy())
            {
                log(LogEntry.Info($"{kind} を配置: {path}") with { Source = pluginName });
                written.Add(path);
            }
        }
        catch (Exception ex)
        {
            log(LogEntry.Warning($"{kind} 配置に失敗（継続）: {ex.Message}") with { Source = pluginName });
        }
    }
}
