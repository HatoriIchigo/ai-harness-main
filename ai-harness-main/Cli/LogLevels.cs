using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// CLI で扱う <see cref="LogLevel"/> の表記。表示・<c>--filter</c> の解釈ともここに集約する。
/// 表示は短縮小文字（<c>warn</c>）だが、解釈は短縮・正式（<c>warning</c>）の両方を大小無視で受ける。
/// </summary>
internal static class LogLevels
{
    /// <summary>表示用の短縮名。</summary>
    public static string Format(LogLevel level) => level switch
    {
        LogLevel.Warning => "warn",
        _ => level.ToString().ToLowerInvariant(),
    };

    /// <summary>レベル名を解釈する。未知の語は <c>false</c>。</summary>
    public static bool TryParse(string text, out LogLevel level)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "trace":
                level = LogLevel.Trace;
                return true;
            case "debug":
                level = LogLevel.Debug;
                return true;
            case "info":
                level = LogLevel.Info;
                return true;
            case "warn":
            case "warning":
                level = LogLevel.Warning;
                return true;
            case "error":
                level = LogLevel.Error;
                return true;
            default:
                level = default;
                return false;
        }
    }
}
