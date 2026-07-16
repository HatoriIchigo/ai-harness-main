using System.Text.Json.Serialization;

namespace ai_harness_main;

/// <summary>
/// daemon → CLI（<c>--project</c>）の応答（フレーム内 UTF8 JSON）。
/// daemon がメモリに展開している（<see cref="ProjectContext"/> を生成済みの）プロジェクトルートを返す。
/// daemon 未起動時はパイプに接続できないため、この応答自体が返らない（CLI 側で判定する）。
/// </summary>
internal sealed class ProjectsResponse
{
    /// <summary>プロジェクトルート（絶対パス）の一覧。daemon 内のキーと同じ正規化済み文字列。</summary>
    [JsonPropertyName("roots")]
    public List<string> Roots { get; set; } = [];
}
