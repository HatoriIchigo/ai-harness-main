using System.Text.Json.Serialization;

namespace ai_harness_main;

/// <summary>
/// daemon → client のパイプ応答ペイロード（フレーム内 UTF8 JSON）。
/// client 側に同一定義の複製がある（Framing と同様、IPC 境界の型を両端で共有しないため）。
/// </summary>
internal sealed class HookResponse
{
    /// <summary>0=許可／非0=deny。</summary>
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    /// <summary>deny 理由（client が stderr へ）。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Claude へ注入する追加コンテキスト（client が hook 出力 JSON へ）。</summary>
    [JsonPropertyName("additionalContext")]
    public string? AdditionalContext { get; set; }
}
