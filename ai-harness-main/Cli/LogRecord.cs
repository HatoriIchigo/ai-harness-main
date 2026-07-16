using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// ログファイル（JSON Lines）1 行分。<see cref="Logger"/> が書く 4 つの基本フィールドに対応し、
/// deny の監査レコードだけが持つ構造化フィールド（<c>event</c>／<c>kind</c>）を併せて読む。
/// </summary>
/// <param name="Timestamp">記録時刻（ローカル）。</param>
/// <param name="Level">重大度。</param>
/// <param name="Source">発生源。ハーネス本体は <c>claude</c>、プラグインはその PluginName。</param>
/// <param name="Message">本文。</param>
/// <param name="IsDeny">deny の監査レコードか（<c>event=deny</c>）。</param>
/// <param name="Kind">deny の由来（<c>rule</c>／<c>failclose</c>）。deny 以外は <c>null</c>。</param>
internal readonly record struct LogRecord(
    DateTime Timestamp, LogLevel Level, string Source, string Message,
    bool IsDeny = false, string? Kind = null);
