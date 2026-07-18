using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 1 つの起動済み LSP プロセスとの JSON-RPC（<c>Content-Length</c> フレーミング）会話を担う。
/// スコープは診断（<c>textDocument/publishDiagnostics</c>）の受信キャッシュのみ。
/// 任意のリクエスト（hover／definition 等）は扱わない
/// （プラグイン実行モデルが同期・使い捨てインスタンスのため、応答待ちでスレッドを塞ぐ設計は避けた。
/// 詳細はプロジェクトの設計議論を参照）。
///
/// <c>initialize</c> はここで唯一の「応答を待つ」リクエストとして使う（ハンドシェイクに必須なため）。
/// 以降はファイル変更のたびに <c>didOpen</c>／<c>didChange</c> を送るだけの一方向通知で、
/// 診断はサーバから非同期に届く通知を受けてキャッシュを更新する（<see cref="GetDiagnosticsByPath"/> で読む）。
/// </summary>
internal sealed class LspProtocolClient : IDisposable
{
    private readonly Process _process;
    private readonly string _language;
    private readonly Action<LogEntry> _log;
    private readonly JsonNode? _initializationSettings;

    private int _nextId;
    private long _nextVersion = 1;
    private volatile bool _initialized;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    // 以下 3 つは全て「正規化済みローカルパス」（<see cref="CanonicalPath"/>）をキーにする。
    // サーバによっては publishDiagnostics の uri をこちらが送った uri と異なる表記（実測: pyright が
    // ドライブレターの : を %3A にパーセントエンコード）で返すため、URI 文字列そのものをキーにすると
    // 送信側と受信側が一致しない（実際にこれが原因で _waiters が一切完了しないバグを起こした）。
    // ローカルパスへ正規化してから突き合わせることで、送受信どちらの表記でも同じキーに収束させる。
    private readonly ConcurrentDictionary<string, IReadOnlyList<LspDiagnostic>> _diagnosticsByPath =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _openedPaths = new(StringComparer.Ordinal);
    // 次の publishDiagnostics を待っている Fire リクエスト（Fire 専用。Action の非同期キャッシュ読みでは使わない）。
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<LspDiagnostic>>> _waiters =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <param name="initializationSettings">
    /// <c>initialized</c> の直後に <c>workspace/didChangeConfiguration</c> の <c>settings</c> として送る値
    /// （<see cref="LspDefinition.InitializationSettings"/>）。<c>null</c> なら送らない（サーバ既定のまま）。
    /// </param>
    public LspProtocolClient(Process process, string language, Action<LogEntry> log, JsonNode? initializationSettings)
    {
        _process = process;
        _language = language;
        _log = log;
        _initializationSettings = initializationSettings;
    }

    /// <summary>読み取りループの開始と <c>initialize</c>/<c>initialized</c> ハンドシェイクをバックグラウンドで進める。</summary>
    public void Start(string projectRoot)
    {
        _ = Task.Run(ReadLoopAsync);
        _ = InitializeAsync(projectRoot);
    }

    /// <summary>
    /// ファイルの現在内容をサーバへ同期する（初回は <c>didOpen</c>、以降は <c>didChange</c>・フルテキスト置換）。
    /// <c>initialize</c> が終わっていなければ何もしない（次の変更で追いつく。ここで待たない）。
    /// 呼び出し元（<see cref="LspManager"/>）が fire-and-forget で呼ぶ前提で、ここでも例外は投げない。
    /// </summary>
    public async Task NotifyFileAsync(string filePath, string content)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            var uri = new Uri(filePath).AbsoluteUri;
            var canonicalPath = CanonicalPath(filePath);
            if (_openedPaths.TryAdd(canonicalPath, true))
            {
                await SendNotificationAsync("textDocument/didOpen", new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["uri"] = uri,
                        ["languageId"] = _language,
                        ["version"] = 1,
                        ["text"] = content,
                    },
                }).ConfigureAwait(false);
            }
            else
            {
                var version = Interlocked.Increment(ref _nextVersion);
                await SendNotificationAsync("textDocument/didChange", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
                    // range を省略した contentChanges はドキュメント全体の置換（フルシンク。差分計算はしない）。
                    ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = content }),
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log(LogEntry.Debug($"{_language}: ファイル同期に失敗 ({filePath}): {ex.Message}") with { Source = "lsp" });
        }
    }

    /// <summary>
    /// <see cref="IFireLspRequester"/> の実体。<paramref name="filePath"/> を同期し、次の
    /// <c>publishDiagnostics</c> が届くまで（または <paramref name="timeout"/> 経過まで）待って返す。
    /// <see cref="NotifyFileAsync"/> と違い、<b>ブロックしてよい</b>（Fire 専用の同期バッチ用途のため）。
    /// タイムアウトしたら、その時点のキャッシュ（古い可能性がある）にフォールバックする
    /// （何も届いていなければ空）。
    /// </summary>
    public async Task<IReadOnlyList<LspDiagnostic>> RequestDiagnosticsAsync(
        string filePath, string content, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        // initialize がまだなら、期限内はポーリングで待つ（Fire は同期バッチのため、ここで待ってよい）。
        while (!_initialized && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200).ConfigureAwait(false);
        }
        if (!_initialized)
        {
            return [];
        }

        string canonicalPath;
        try
        {
            canonicalPath = CanonicalPath(filePath);
        }
        catch
        {
            return [];
        }

        var tcs = new TaskCompletionSource<IReadOnlyList<LspDiagnostic>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[canonicalPath] = tcs;
        try
        {
            await NotifyFileAsync(filePath, content).ConfigureAwait(false);

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return _diagnosticsByPath.TryGetValue(canonicalPath, out var immediate) ? immediate : [];
            }

            using var cts = new CancellationTokenSource(remaining);
            await using var registration = cts.Token.Register(() => tcs.TrySetResult(
                _diagnosticsByPath.TryGetValue(canonicalPath, out var cached) ? cached : []));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _waiters.TryRemove(canonicalPath, out _);
        }
    }

    /// <summary>直近キャッシュした診断のスナップショット（正規化済み絶対ファイルパス → 診断一覧）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> GetDiagnosticsByPath() =>
        new Dictionary<string, IReadOnlyList<LspDiagnostic>>(_diagnosticsByPath, StringComparer.Ordinal);

    /// <summary>
    /// ファイルパスの正規化キー。<see cref="Path.GetFullPath(string)"/> に加えドライブレターを大文字に揃える
    /// （<see cref="TryGetLocalPath"/> 側の正規化と一致させるため。揃えないと大文字小文字を区別する
    /// 辞書キーとして食い違う）。
    /// </summary>
    private static string CanonicalPath(string rawPath)
    {
        var full = Path.GetFullPath(rawPath);
        if (full.Length >= 2 && full[1] == ':' && char.IsAsciiLetter(full[0]))
        {
            full = char.ToUpperInvariant(full[0]) + full[1..];
        }
        return full;
    }

    /// <summary>
    /// <c>file://</c> URI を正規化済みローカルパスへ変換する。<see cref="Uri.LocalPath"/> をそのまま使わない理由:
    /// サーバによっては（実測: pyright）<c>publishDiagnostics</c> の <c>uri</c> でドライブレターの <c>:</c> を
    /// <c>%3A</c> にパーセントエンコードして返してくることがあり、この状態だと <c>Uri</c> はもう
    /// ドライブレター付きローカルパスとして認識できず、<c>LocalPath</c> が <c>/c:/Users/...</c> のような
    /// 壊れた値になる（実測で確認）。<c>AbsolutePath</c>（生のパス部分）を自前でデコード・正規化することで、
    /// エンコードされていてもいなくても <see cref="CanonicalPath"/> と同じキーに揃える。
    /// </summary>
    private static string? TryGetLocalPath(string uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return null;
        }

        var path = Uri.UnescapeDataString(parsed.AbsolutePath);
        // "/c:/Users/..." 形式（先頭 / ＋ ドライブレター）ならドライブレター付きローカルパスへ正規化する。
        if (path.Length >= 3 && path[0] == '/' && path[2] == ':' && char.IsAsciiLetter(path[1]))
        {
            path = char.ToUpperInvariant(path[1]) + path[2..];
        }
        path = path.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            return CanonicalPath(path);
        }
        catch
        {
            return path; // GetFullPath できない形ならそのまま返す。
        }
    }

    private async Task InitializeAsync(string projectRoot)
    {
        try
        {
            var rootUri = new Uri(projectRoot).AbsoluteUri;
            var initParams = new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = rootUri,
                ["capabilities"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["publishDiagnostics"] = new JsonObject(),
                    },
                },
            };
            await SendRequestAsync("initialize", initParams, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            await SendNotificationAsync("initialized", new JsonObject()).ConfigureAwait(false);

            if (_initializationSettings is not null)
            {
                // カタログの静的インスタンスを共有しているため、送信のたびに複製する
                // （JsonNode は単一の親にしか属せない）。
                await SendNotificationAsync("workspace/didChangeConfiguration", new JsonObject
                {
                    ["settings"] = _initializationSettings.DeepClone(),
                }).ConfigureAwait(false);
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            _log(LogEntry.Warning($"{_language}: LSP initialize に失敗（診断は使えない）: {ex.Message}") with { Source = "lsp" });
        }
    }

    private async Task ReadLoopAsync()
    {
        var stream = _process.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                var message = await ReadMessageAsync(stream).ConfigureAwait(false);
                if (message is null)
                {
                    break; // EOF（プロセス終了等）。
                }
                HandleMessage(message);
            }
        }
        catch (Exception ex)
        {
            _log(LogEntry.Debug($"{_language}: LSP 読み取りループ終了: {ex.Message}") with { Source = "lsp" });
        }
        finally
        {
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetCanceled();
            }
            _pending.Clear();
        }
    }

    private void HandleMessage(JsonNode message)
    {
        if (message["method"] is null && message["id"] is { } idNode)
        {
            // レスポンス（id はあるが method が無い）。
            if (!idNode.AsValue().TryGetValue<int>(out var id) || !_pending.TryRemove(id, out var tcs))
            {
                return;
            }
            if (message["error"] is { } error)
            {
                tcs.TrySetException(new InvalidOperationException($"LSP error: {error}"));
            }
            else
            {
                tcs.TrySetResult(message["result"]);
            }
            return;
        }

        if (message["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")
        {
            HandlePublishDiagnostics(message["params"]);
        }
        // window/logMessage 等の他の通知は v1 スコープ外のため無視。
    }

    private void HandlePublishDiagnostics(JsonNode? paramsNode)
    {
        var uri = paramsNode?["uri"]?.GetValue<string>();
        if (uri is null || TryGetLocalPath(uri) is not { } canonicalPath)
        {
            return;
        }

        var diagnostics = new List<LspDiagnostic>();
        if (paramsNode?["diagnostics"] is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is null)
                {
                    continue;
                }
                var start = item["range"]?["start"];
                var end = item["range"]?["end"];
                diagnostics.Add(new LspDiagnostic
                {
                    Severity = SeverityName(item["severity"]?.GetValue<int?>()),
                    StartLine = start?["line"]?.GetValue<int>() ?? 0,
                    StartColumn = start?["character"]?.GetValue<int>() ?? 0,
                    EndLine = end?["line"]?.GetValue<int>() ?? 0,
                    EndColumn = end?["character"]?.GetValue<int>() ?? 0,
                    Message = item["message"]?.GetValue<string>() ?? "",
                    Source = item["source"]?.GetValue<string>(),
                    Code = item["code"]?.ToJsonString(),
                });
            }
        }
        _diagnosticsByPath[canonicalPath] = diagnostics;
        if (_waiters.TryRemove(canonicalPath, out var waiter))
        {
            waiter.TrySetResult(diagnostics);
        }
    }

    private static string SeverityName(int? severity) => severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "information",
        4 => "hint",
        _ => "unknown",
    };

    private async Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await WriteMessageAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params,
        }).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(timeout);
        await using var registration = cts.Token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task.ConfigureAwait(false);
    }

    private Task SendNotificationAsync(string method, JsonNode? @params) =>
        WriteMessageAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        });

    private async Task WriteMessageAsync(JsonNode message)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var stream = _process.StandardInput.BaseStream;
            await stream.WriteAsync(headerBytes).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// <c>Content-Length</c> ヘッダ＋空行＋ちょうどその長さの UTF-8 JSON、という LSP のフレーミングで 1 メッセージ読む。
    /// ヘッダは ASCII 前提の行読み（<c>\n</c> 区切り）。本文はヘッダで宣言されたバイト数を正確に読む
    /// （<see cref="StreamReader"/> の内部バッファと直接ストリーム読みを混在させると取りこぼすため、
    /// ヘッダ・本文とも生の <see cref="Stream"/> だけで読む）。
    /// </summary>
    private static async Task<JsonNode?> ReadMessageAsync(Stream stream)
    {
        var contentLength = -1;
        var lineBytes = new List<byte>();
        while (true)
        {
            var b = stream.ReadByte();
            if (b == -1)
            {
                return null; // EOF。
            }
            if (b != '\n')
            {
                lineBytes.Add((byte)b);
                continue;
            }

            var line = Encoding.ASCII.GetString(lineBytes.ToArray()).TrimEnd('\r');
            lineBytes.Clear();
            if (line.Length == 0)
            {
                break; // 空行でヘッダ終了。
            }
            var separator = line.IndexOf(':');
            if (separator > 0
                && string.Equals(line[..separator].Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(separator + 1)..].Trim(), out var parsed))
            {
                contentLength = parsed;
            }
        }

        if (contentLength < 0)
        {
            return null; // Content-Length が無いメッセージは扱えない。
        }

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await stream.ReadAsync(body.AsMemory(read, contentLength - read)).ConfigureAwait(false);
            if (n == 0)
            {
                return null; // 本文の途中で EOF。
            }
            read += n;
        }
        return JsonNode.Parse(body);
    }

    public void Dispose()
    {
        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetCanceled();
        }
        _pending.Clear();
        _writeLock.Dispose();
    }
}
