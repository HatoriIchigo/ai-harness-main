using System.Text.Json;
using System.Text.Json.Nodes;

namespace ai_harness_main;

/// <summary>
/// <c>&lt;プロジェクトルート&gt;/.claude/settings.json</c> に、<c>ai-harness-main</c> を叩く
/// <c>SessionStart</c>／<c>PreToolUse</c>／<c>PostToolUse</c> hook を追記する（<c>--init</c> の実体の一部）。
///
/// <c>SessionStart</c> はプラグインの発火に加えて rule/skill の配布契機を兼ねる
/// （<see cref="ResourceDistributor"/>）。Claude Code はセッション初期化時に skill を走査するため、
/// この配線が無いと配布がツール使用時まで遅れ、その回のセッションには載らない。
///
/// 既存の設定（他ツールの hook・permissions 等）は保持し、追記のみ行う。各イベントについて、
/// 既に <c>command</c> が <c>ai-harness-main</c> の hook エントリがあれば「配線済み」とみなし、
/// 二重に追加しない（<c>matcher</c> の値までは問わない）。ファイルが無ければ新規作成する。
/// </summary>
internal static class SettingsJsonEditor
{
    private const string HookCommand = "ai-harness-main";

    /// <summary>配線するイベント。既に配線済みのイベントは飛ばすため、順序は新規作成時の出力順のみに効く。</summary>
    public static readonly string[] HookEvents = ["SessionStart", "PreToolUse", "PostToolUse"];

    /// <summary>
    /// <paramref name="projectRoot"/> の <c>.claude/settings.json</c> を確認・追記する。
    /// 戻り値の <c>Added</c> は今回追記したイベント名（既に全て配線済みなら空＝ファイルは書き換えない）。
    /// 一部のイベントだけ配線済みの既存プロジェクトもあるため、「追記したもの」だけを返す。
    /// </summary>
    public static (IReadOnlyList<string> Added, string? Error) EnsureHooks(string projectRoot)
    {
        var claudeDir = Path.Combine(projectRoot, ".claude");
        var path = Path.Combine(claudeDir, "settings.json");

        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (Exception ex)
        {
            return ([], $"settings.json の解析に失敗（壊れているため自動編集できません）: {ex.Message}");
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var added = new List<string>();
        foreach (var eventName in HookEvents)
        {
            if (hooks[eventName] is not JsonArray matcherEntries)
            {
                matcherEntries = new JsonArray();
                hooks[eventName] = matcherEntries;
            }

            if (HasHookCommand(matcherEntries))
            {
                continue; // 既に ai-harness-main を叩く hook がある（配線済み）。
            }

            matcherEntries.Add(new JsonObject
            {
                ["matcher"] = "*",
                ["hooks"] = new JsonArray
                {
                    new JsonObject { ["type"] = "command", ["command"] = HookCommand },
                },
            });
            added.Add(eventName);
        }

        if (added.Count == 0)
        {
            return ([], null);
        }

        try
        {
            Directory.CreateDirectory(claudeDir);
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            return ([], $"settings.json の書き込みに失敗: {ex.Message}");
        }

        return (added, null);
    }

    /// <summary><paramref name="matcherEntries"/>（1 イベント分）のどこかに <c>ai-harness-main</c> を叩く hook があるか。</summary>
    private static bool HasHookCommand(JsonArray matcherEntries)
    {
        foreach (var entry in matcherEntries)
        {
            if (entry is not JsonObject obj || obj["hooks"] is not JsonArray innerHooks)
            {
                continue;
            }
            foreach (var hook in innerHooks)
            {
                if (hook is JsonObject hookObj
                    && hookObj["command"]?.GetValue<string>() == HookCommand)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
