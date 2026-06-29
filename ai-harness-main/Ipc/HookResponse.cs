using System.Text.Json.Serialization;

namespace ai_harness_main;

/// <summary>
/// daemon → bridge のパイプ応答ペイロード（フレーム内 UTF8 JSON）。
/// </summary>
internal sealed class HookResponse
{
    /// <summary>0=許可／非0=deny。</summary>
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    /// <summary>deny 理由（bridge が stderr へ）。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Claude へ注入する追加コンテキスト（bridge が hook 出力 JSON へ）。</summary>
    [JsonPropertyName("additionalContext")]
    public string? AdditionalContext { get; set; }
}
