using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>deny の由来。監査上まったく意味が違うため区別する。</summary>
internal enum DenyKind
{
    /// <summary>プラグインがルールに従って拒否した（設計どおりの動作）。</summary>
    Rule,

    /// <summary>プラグインを検証できずにブロックした（生成失敗・クラッシュ・設定不良）。</summary>
    FailClose,
}

/// <summary>
/// 1 プラグインが 1 リクエストを deny した事実。ログの本文に日本語を溶かし込む代わりに、
/// フィールドとして記録して機械的に集計・絞り込みできるようにする。
/// </summary>
/// <param name="Plugin">deny したプラグインの PluginName（生成失敗時は型名）。</param>
/// <param name="Kind">ルールによる deny か、フェイルクローズか。</param>
/// <param name="Reason">Claude Code へ返す理由（全文）。</param>
/// <param name="Tool">対象ツール名。hook に無ければ <c>null</c>。</param>
/// <param name="HookEvent">hook イベント名。無ければ <c>null</c>。</param>
internal sealed record DenyEvent(
    string Plugin, DenyKind Kind, string Reason, string? Tool, string? HookEvent)
{
    /// <summary>ログ本文（1 行）。既存の読み手は message しか見ないため、ここだけで概要が分かるようにする。</summary>
    public string Summary()
    {
        var target = Tool is null ? HookEvent ?? "(unknown)" : $"{HookEvent}/{Tool}";
        var label = Kind == DenyKind.FailClose ? "フェイルクローズでブロック" : "deny";
        return $"{label} [{target}]: {Reason}";
    }

    /// <summary>JSON 1 行へ添える構造化フィールド。</summary>
    public IReadOnlyDictionary<string, string?> ToFields() => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["event"] = "deny",
        ["kind"] = Kind == DenyKind.FailClose ? "failclose" : "rule",
        ["plugin"] = Plugin,
        ["tool"] = Tool,
        ["hookEvent"] = HookEvent,
        ["reason"] = Reason,
    };
}

/// <summary><see cref="DenyKind"/> の付随情報。</summary>
internal static class DenyKinds
{
    /// <summary>
    /// 記録するログレベル。ルール deny は設計どおりの動作なので <see cref="LogLevel.Warning"/>、
    /// フェイルクローズはハーネス側の異常なので <see cref="LogLevel.Error"/>。
    /// これにより <c>--filter error</c> が「ハーネスの不調」だけを拾える。
    /// </summary>
    public static LogLevel Level(this DenyKind kind) =>
        kind == DenyKind.FailClose ? LogLevel.Error : LogLevel.Warning;
}
