using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// レベル閾値でフィルタし、stderr（人間可読）と単一ログファイル <c>logs/&lt;yyyy-MM-dd&gt;.jsonl</c>
/// （JSON Lines）へ書く logger。ログはこの 1 系統に集約する（プラグイン・claude を問わず）。
/// 閾値未満のレベルは破棄。stdout は Claude が解釈し得るため使わない。
/// プラグインは並列発火するため、ファイル追記はロックで直列化する。
/// </summary>
internal sealed class Logger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly LogLevel _minLevel;
    private readonly bool _toStderr;
    private readonly string _logDir;
    private readonly object _fileLock = new();

    /// <param name="minLevel">出力閾値。</param>
    /// <param name="logDir">ログファイルの出力先ディレクトリ（プロジェクト個別の
    /// <c>&lt;ルート&gt;/.claude/harness/logs</c>、または daemon ライフサイクル用のグローバル log）。</param>
    /// <param name="toStderr">stderr へも出すか。daemon 常駐時は false（stderr に消費者がいないため）。</param>
    public Logger(LogLevel minLevel, string logDir, bool toStderr = true)
    {
        _minLevel = minLevel;
        _toStderr = toStderr;
        _logDir = logDir;
    }

    /// <summary>
    /// <see cref="LogEntry"/> を閾値判定して出力する。source 未設定は claude（ハーネス由来）。
    /// stderr へは <c>[LEVEL] &lt;source&gt;: メッセージ</c>、ファイルへは JSON 1 行で記録。
    /// </summary>
    public void Emit(LogEntry entry) => Emit(entry, fields: null);

    /// <summary>
    /// 構造化フィールドを添えて出力する。<paramref name="fields"/> は JSON 1 行へそのまま追加され、
    /// stderr の人間向け表示には出ない。既定の 4 キー（timestamp／level／source／message）は上書きしない。
    ///
    /// 既存の読み手は message だけを見るため、フィールドを増やしても後方互換。
    /// </summary>
    public void Emit(LogEntry entry, IReadOnlyDictionary<string, string?>? fields)
    {
        if (entry.Level < _minLevel)
        {
            return;
        }

        var source = string.IsNullOrEmpty(entry.Source) ? "claude" : entry.Source;

        if (_toStderr)
        {
            Console.Error.WriteLine($"[{entry.Level.ToString().ToUpperInvariant()}] {source}: {entry.Message}");
        }

        var record = new JsonObject
        {
            ["timestamp"] = DateTime.Now.ToString("o"),
            ["level"] = entry.Level.ToString(),
            ["source"] = source,
            ["message"] = entry.Message,
        };
        if (fields is not null)
        {
            foreach (var (key, value) in fields.Where(f => !record.ContainsKey(f.Key)))
            {
                record[key] = value;
            }
        }
        AppendToFile(record.ToJsonString(JsonOptions));
    }

    /// <summary>レベルとメッセージから直接出力する（main 内部＝source claude）。</summary>
    public void Write(LogLevel level, string message) => Emit(new LogEntry(level, message, "claude"));

    /// <summary>
    /// deny を監査レコードとして記録する。ルールによる deny（<see cref="LogLevel.Warning"/>）と、
    /// 検証できずにブロックしたフェイルクローズ（<see cref="LogLevel.Error"/>）をレベルで分ける。
    /// </summary>
    public void WriteDeny(DenyEvent deny) => Emit(
        new LogEntry(deny.Kind.Level(), deny.Summary(), deny.Plugin), deny.ToFields());

    private void AppendToFile(string line)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(_logDir);
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // ログファイル書き込みの失敗で本処理を止めない（stderr には既に出力済み）。
        }
    }

    /// <summary>当日分の統合ログファイル（<c>logs/&lt;yyyy-MM-dd&gt;.jsonl</c>）。</summary>
    private string LogFilePath => Path.Combine(_logDir, $"{DateTime.Now:yyyy-MM-dd}.jsonl");
}
