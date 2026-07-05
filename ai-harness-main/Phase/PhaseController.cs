using System.Text;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// main コアが直接処理する「フェーズ制御コマンド」。<c>UserPromptSubmit</c> の prompt 先頭が
/// <c>/harness-next-phase</c> / <c>/harness-next-phase-help</c> のとき、プラグインを介さず main が処理する。
///
/// 応答は<b>ブロックしない</b>（exit 0）。結果・エラーは additionalContext（非ブロックのコンテキスト注入）で返す。
/// 「次フェーズが無い／曖昧」は準正常として、失敗にせず案内メッセージを返す。
/// フェーズ定義は <see cref="PhaseGraph"/>（config/phase.yml）、現在フェーズは <see cref="StateStore"/>（state.json の phase）。
/// </summary>
internal static class PhaseController
{
    /// <summary>次フェーズへ移行するコマンド。引数（移行先）は省略可。</summary>
    public const string CmdNext = "/harness-next-phase";

    /// <summary>現在フェーズと次候補を案内するコマンド。</summary>
    public const string CmdHelp = "/harness-next-phase-help";

    /// <summary>prompt がフェーズ制御コマンドか（先頭トークンで判定）。</summary>
    public static bool IsCommand(string? prompt)
    {
        var tokens = Tokenize(prompt);
        var cmd = tokens.Length > 0 ? tokens[0] : "";
        return cmd == CmdNext || cmd == CmdHelp;
    }

    /// <summary>コマンドを処理し、非ブロック（additionalContext 付き）の判定を返す。</summary>
    public static HostDecision Handle(string configDir, StateStore state, string prompt, Action<LogEntry> log)
    {
        var graph = PhaseGraph.Load(configDir);
        var tokens = Tokenize(prompt);
        var cmd = tokens.Length > 0 ? tokens[0] : "";

        if (cmd == CmdHelp)
        {
            return Help(graph, state);
        }
        // CmdNext。第2トークンがあれば移行先の明示指定。
        var arg = tokens.Length > 1 ? tokens[1] : null;
        return Next(graph, state, arg, log);
    }

    private static HostDecision Next(PhaseGraph graph, StateStore state, string? arg, Action<LogEntry> log)
    {
        var current = state.GetPhase() ?? graph.Initial;
        if (current is null)
        {
            return Info($"フェーズが未定義です（{PhaseGraph.FileName} が空か不正）。");
        }

        var successors = graph.Next(current);
        string target;

        if (arg is not null)
        {
            // 明示指定: 現在フェーズの正当な次フェーズでなければエラー案内。
            if (!successors.Contains(arg))
            {
                return Info(
                    $"'{arg}' は現在フェーズ '{current}' の次フェーズではありません。" +
                    $"移行できるのは: {Join(successors)}");
            }
            target = arg;
        }
        else if (successors.Count == 1)
        {
            // 次が一意 → 省略で自動遷移。
            target = successors[0];
        }
        else if (successors.Count == 0)
        {
            // 準正常: 最終フェーズで次が無い（失敗ではない）。
            return Info($"現在フェーズ '{current}' は最終フェーズで、次に移行できるフェーズがありません。");
        }
        else
        {
            // 曖昧: 複数候補があるため明示指定を促す。
            return Info(
                $"現在フェーズ '{current}' からは次フェーズが複数あります。" +
                $"`{CmdNext} <phase>` で指定してください: {Join(successors)}");
        }

        state.SetPhase(target);
        log(LogEntry.Info($"フェーズ移行 {current} -> {target}"));

        var desc = graph.Desc(target);
        var descPart = string.IsNullOrWhiteSpace(desc) ? "" : $"\nこのフェーズでやること: {desc}";
        return Info($"フェーズを '{current}' から '{target}' に移行しました。{descPart}");
    }

    private static HostDecision Help(PhaseGraph graph, StateStore state)
    {
        var current = state.GetPhase() ?? graph.Initial;
        if (current is null)
        {
            return Info($"フェーズが未定義です（{PhaseGraph.FileName} が空か不正）。");
        }

        var desc = graph.Desc(current);
        var successors = graph.Next(current);

        var sb = new StringBuilder();
        sb.Append($"現在のフェーズ: {current}");
        if (!string.IsNullOrWhiteSpace(desc))
        {
            sb.Append($"\nこのフェーズでやること: {desc}");
        }
        if (successors.Count == 0)
        {
            sb.Append("\n次に移行できるフェーズ: なし（最終フェーズ）");
        }
        else
        {
            sb.Append("\n次に移行できるフェーズ:");
            foreach (var s in successors)
            {
                var sd = graph.Desc(s);
                sb.Append(string.IsNullOrWhiteSpace(sd) ? $"\n  - {s}" : $"\n  - {s}: {sd}");
            }
            sb.Append($"\n（移行: `{CmdNext}`");
            sb.Append(successors.Count == 1 ? "（省略で自動））" : $" もしくは `{CmdNext} <phase>`）");
        }
        return Info(sb.ToString());
    }

    /// <summary>非ブロック（exit 0）＋ additionalContext でメッセージを返す。</summary>
    private static HostDecision Info(string message) => new(0, null, message);

    private static string Join(IReadOnlyList<string> items) => string.Join(", ", items);

    private static string[] Tokenize(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt)
            ? Array.Empty<string>()
            : prompt.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
