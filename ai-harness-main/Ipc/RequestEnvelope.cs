using System.Text.Json.Serialization;

namespace ai_harness_main;

/// <summary>
/// bridge（hook 受け口）→ daemon のリクエスト封筒（フレーム内 UTF8 JSON）。
/// 単一 daemon が複数プロジェクトをさばくため、どのプロジェクトの hook かを <see cref="ProjectRoot"/> で運ぶ。
/// daemon は常駐ゆえ各 hook プロセスの cwd/環境を持たない。プロジェクト識別は bridge が解決して同梱する。
/// </summary>
internal sealed class RequestEnvelope
{
    public const string TypeHook = "hook";
    public const string TypeStop = "stop";

    /// <summary>メモリ上のプロジェクト一覧の照会（<c>--project</c>）。応答は <see cref="ProjectsResponse"/>。</summary>
    public const string TypeProjects = "projects";

    /// <summary>プラグインの能動スキャン起動（<c>--fire</c>）。応答は <see cref="FireResponse"/>。</summary>
    public const string TypeFire = "fire";

    /// <summary>
    /// リクエスト種別。<see cref="TypeHook"/> / <see cref="TypeStop"/> / <see cref="TypeProjects"/> /
    /// <see cref="TypeFire"/>。
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = TypeHook;

    /// <summary>プロジェクトルート（<c>.claude</c> を含む階層の絶対パス）。hook / fire 時に必須。</summary>
    [JsonPropertyName("projectRoot")]
    public string? ProjectRoot { get; set; }

    /// <summary>Claude Code から渡された生の hook JSON 文字列（そのまま）。hook 時に必須。</summary>
    [JsonPropertyName("hookJson")]
    public string? HookJson { get; set; }

    /// <summary>
    /// fire 対象を 1 プラグインへ絞り込む PluginName。null／空は全プラグイン対象（<c>--fire</c>）。
    /// </summary>
    [JsonPropertyName("pluginName")]
    public string? PluginName { get; set; }
}
