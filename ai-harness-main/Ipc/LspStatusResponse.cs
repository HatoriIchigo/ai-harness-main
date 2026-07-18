using System.Text.Json.Serialization;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// daemon → CLI（<c>--lsp &lt;プロジェクト&gt;</c>）の応答（フレーム内 UTF8 JSON）。
/// <see cref="LspManager.GetStatusSnapshot"/> をそのまま運ぶ。プロジェクトが daemon のメモリ上に無ければ
/// （hook が一度も来ていない等）空になる（daemon はこの照会のためにプロジェクトを新規生成しない）。
/// </summary>
internal sealed class LspStatusResponse
{
    /// <summary>言語名 → 状態。</summary>
    [JsonPropertyName("languages")]
    public Dictionary<string, LspLanguageState> Languages { get; set; } = [];
}
