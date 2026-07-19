using System.Text.Json;
using System.Text.Json.Nodes;

namespace ai_harness_main;

/// <summary>
/// <c>&lt;プロジェクトルート&gt;/.claude/settings.json</c> に、<c>ai-harness-main</c> を叩く
/// <c>PreToolUse</c>／<c>PostToolUse</c> hook を追記する（<c>--init</c> の実体の一部）。
///
/// 既存の設定（他ツールの hook・permissions 等）は保持し、追記のみ行う。各イベントについて、
/// 既に <c>command</c> が <c>ai-harness-main</c> の hook エントリがあれば「配線済み」とみなし、
/// 二重に追加しない（<c>matcher</c> の値までは問わない）。ファイルが無ければ新規作成する。
/// </summary>
internal static class SettingsJsonEditor
{
    private const string HookCommand = "ai-harness-main";
    private static readonly string[] HookEvents = ["PreToolUse", "PostToolUse"];

    /// <summary>
    /// <paramref name="projectRoot"/> の <c>.claude/settings.json</c> を確認・追記する。
    /// 戻り値の <c>Changed</c> は実際にファイルを書き換えたか（既に配線済みなら <c>false</c>）。
    /// </summary>
    public static (bool Changed, string? Error) EnsureHooks(string projectRoot)
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
            return (false, $"settings.json の解析に失敗（壊れているため自動編集できません）: {ex.Message}");
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var changed = false;
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
            changed = true;
        }

        if (!changed)
        {
            return (false, null);
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
            return (false, $"settings.json の書き込みに失敗: {ex.Message}");
        }

        return (true, null);
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
