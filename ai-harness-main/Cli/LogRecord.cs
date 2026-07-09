using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>ログファイル（JSON Lines）1 行分。<see cref="Logger"/> が書く 4 フィールドに対応する。</summary>
/// <param name="Timestamp">記録時刻（ローカル）。</param>
/// <param name="Level">重大度。</param>
/// <param name="Source">発生源。ハーネス本体は <c>claude</c>、プラグインはその PluginName。</param>
/// <param name="Message">本文。</param>
internal readonly record struct LogRecord(DateTime Timestamp, LogLevel Level, string Source, string Message);
