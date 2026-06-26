using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ai_harness_main;

/// <summary>
/// Claude Code の hook 出力 JSON（<c>hookSpecificOutput.additionalContext</c>）を組み立てる。
/// 非ブロックのコンテキスト注入用。client 側に同一定義の複製がある。
/// </summary>
internal static class HookOutput
{
    /// <summary>
    /// additionalContext を載せた hook 出力 JSON を返す。PreToolUse のときのみ
    /// <c>permissionDecision=allow</c> を併記し、ツール実行をブロックせず文脈へ反映させる。
    /// </summary>
    public static string BuildAdditionalContext(string? hookEventName, string additionalContext)
    {
        var inner = new JsonObject
        {
            ["hookEventName"] = hookEventName ?? string.Empty,
        };
        // permissionDecision は PreToolUse（および権限系）でのみ有効。allow で非ブロック。
        if (hookEventName == "PreToolUse")
        {
            inner["permissionDecision"] = "allow";
        }
        inner["additionalContext"] = additionalContext;

        var root = new JsonObject { ["hookSpecificOutput"] = inner };
        return root.ToJsonString(JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
