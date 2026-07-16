using System.Text.Json.Serialization;

namespace ai_harness_main;

/// <summary>
/// daemon → CLI（<c>--fire</c>）の応答（フレーム内 UTF8 JSON）。
/// プラグインごとの能動スキャン結果を運ぶ。hook のような deny 集約は行わず、各プラグインの結果を
/// そのまま並べる（<c>--fire</c> はゲートではなくレポート）。<see cref="Error"/> が非 null のときは
/// スキャンを実行できなかった（設定不備・内部エラー等）ことを表し、<see cref="Plugins"/> は空。
/// </summary>
internal sealed class FireResponse
{
    /// <summary>スキャンを実行できなかった理由。実行できたら null。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>実行したプラグインごとの結果。</summary>
    [JsonPropertyName("plugins")]
    public List<FirePluginResult> Plugins { get; set; } = [];

    /// <summary>ドメインの <see cref="FireReport"/> を応答 DTO へ写す。</summary>
    public static FireResponse From(FireReport report) => new()
    {
        Error = report.Error,
        Plugins = report.Plugins.Select(FirePluginResult.From).ToList(),
    };
}

/// <summary>1 プラグインの <c>--fire</c> 結果（<see cref="FireResponse"/> の要素）。</summary>
internal sealed class FirePluginResult
{
    /// <summary>PluginName。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Fire の結果コード。0=正常。非 0=スキャンが問題を検出（またはスキャン自体が失敗）。</summary>
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    /// <summary>非 0 のときの理由。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>プラグインが添えた追加コンテキスト（あれば）。</summary>
    [JsonPropertyName("additionalContext")]
    public string? AdditionalContext { get; set; }

    /// <summary>Fire が yield したログ行（整形済み <c>[level] message</c>）。</summary>
    [JsonPropertyName("logs")]
    public List<string> Logs { get; set; } = [];

    /// <summary>0=正常。</summary>
    [JsonIgnore]
    public bool IsOk => ExitCode == 0;

    /// <summary>ドメインの <see cref="FireOutcome"/> を応答 DTO へ写す。</summary>
    public static FirePluginResult From(FireOutcome outcome) => new()
    {
        Name = outcome.Name,
        ExitCode = outcome.Result.ExitCode,
        Reason = outcome.Result.Reason,
        AdditionalContext = outcome.Result.AdditionalContext,
        Logs = outcome.Logs
            .Select(e => $"[{LogLevels.Format(e.Level)}] {e.Message}")
            .ToList(),
    };
}
