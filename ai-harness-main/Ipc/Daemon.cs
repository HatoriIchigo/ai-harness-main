using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 常駐モード（単一・共有 daemon）。起動時にプラグイン型を 1 回発見し、名前付きパイプで接続を待ち受け、
/// 接続ごとに封筒（<see cref="RequestEnvelope"/>）を処理する。bridge が hook ごとに接続してくる。
///
/// マルチテナント: プロジェクトルートをキーに <see cref="ProjectContext"/> を遅延生成・キャッシュし、
/// そのプロジェクトの設定・ログ・有効プラグインで処理する。lib（型）は全プロジェクト共有。
/// アイドル回収: 一定時間アクセスの無いプロジェクトはスイーパが回収（メモリ解放）。接続が一定時間
/// 全く無ければ daemon 自体を終了する（Claude Code 終了後の居座り防止）。
/// </summary>
internal static class Daemon
{
    /// <summary>接続が無い状態がこれを超えたら daemon を終了する。</summary>
    private static readonly TimeSpan IdleShutdown = TimeSpan.FromMinutes(5);

    /// <summary>プロジェクトが無アクセスでこれを超えたらキャッシュを回収する。</summary>
    private static readonly TimeSpan ProjectEvictAfter = TimeSpan.FromMinutes(5);

    /// <summary>スイーパの走査間隔。</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    // プロジェクトルート → コンテキスト。Lazy で生成の二重起動を防ぐ。
    private static readonly ConcurrentDictionary<string, Lazy<ProjectContext>> Projects =
        new(StringComparer.Ordinal);

    private static PluginRegistry _registry = null!;
    private static Logger _globalLog = null!;

    // 全プロジェクト回収（メモリが空）で能動的に終了させるためのシグナル。
    private static CancellationTokenSource _shutdownCts = null!;

    /// <summary>daemon 本体。多重起動はロックファイルで抑止。</summary>
    public static async Task<int> RunAsync(Logger globalLog)
    {
        _globalLog = globalLog;

        FileStream lockFile;
        var lockPath = Path.Combine(InstallPaths.RunDir, "daemon.lock");
        try
        {
            Directory.CreateDirectory(InstallPaths.RunDir);
            lockFile = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }
        catch
        {
            globalLog.Write(LogLevel.Info, "別の daemon が稼働中。起動を中止。");
            return 0;
        }

        try
        {
            // 型発見はプロセス寿命で 1 回（全プロジェクト共有）。
            _registry = new PluginRegistry(globalLog.Emit, InstallPaths.LibDir);
            var pipeName = HarnessPipe.Name();
            globalLog.Write(LogLevel.Info, $"daemon 起動 pipe={pipeName} 共有プラグイン型={_registry.Count}");

            _shutdownCts = new CancellationTokenSource();
            using var shutdownCts = _shutdownCts;
            var sweeper = SweeperLoopAsync(_shutdownCts.Token);

            try
            {
                await AcceptLoopAsync(pipeName, globalLog).ConfigureAwait(false);
            }
            finally
            {
                _shutdownCts.Cancel();
                await sweeper.ConfigureAwait(false);
                DisposeAllProjects();
            }
            return 0;
        }
        finally
        {
            lockFile.Dispose();
        }
    }

    /// <summary>接続受付ループ。<see cref="IdleShutdown"/> の間 1 件も接続が無ければ終了する。</summary>
    private static async Task AcceptLoopAsync(string pipeName, Logger globalLog)
    {
        while (true)
        {
            var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            // アイドル（接続途絶）と、スイーパからの能動的終了シグナルの両方で受付を打ち切る。
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            idleCts.CancelAfter(IdleShutdown);
            try
            {
                await server.WaitForConnectionAsync(idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                if (_shutdownCts.IsCancellationRequested)
                {
                    return; // スイーパが全プロジェクト回収で終了を要求（ログはスイーパ側で記録済み）
                }
                SweepStaleProjects();
                if (Projects.IsEmpty)
                {
                    globalLog.Write(LogLevel.Info, "アイドルタイムアウトで daemon 終了。");
                    return;
                }
                continue; // まだ生存中のプロジェクトがある
            }

            _ = HandleAsync(server, globalLog);
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream server, Logger globalLog)
    {
        try
        {
            byte[] payload;
            try
            {
                payload = await Framing.ReadFrameAsync(server).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                // フレーム無しで切断された接続（IsRunning の死活プローブ等）。無害ゆえ静かに返す。
                return;
            }
            var text = Encoding.UTF8.GetString(payload);

            RequestEnvelope? env;
            try
            {
                env = JsonSerializer.Deserialize<RequestEnvelope>(text);
            }
            catch (Exception ex)
            {
                globalLog.Write(LogLevel.Error, $"封筒の解析に失敗: {ex.Message}");
                await RespondAsync(server, 0, null).ConfigureAwait(false); // fail-open
                return;
            }

            if (env is null)
            {
                await RespondAsync(server, 0, null).ConfigureAwait(false);
                return;
            }

            if (env.Type == RequestEnvelope.TypeStop)
            {
                await RespondAsync(server, 0, "stopping").ConfigureAwait(false);
                globalLog.Write(LogLevel.Info, "stop 要求で daemon 終了。");
                Environment.Exit(0);
            }

            HostDecision decision;
            try
            {
                if (string.IsNullOrEmpty(env.ProjectRoot) || string.IsNullOrEmpty(env.HookJson))
                {
                    throw new InvalidOperationException("projectRoot または hookJson が空。");
                }
                var data = HookData.Parse(env.HookJson);
                var ctx = GetOrCreateProject(env.ProjectRoot);
                decision = await ctx.RunAsync(data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                globalLog.Write(LogLevel.Error, $"hook 処理失敗: {ex.Message}");
                decision = new HostDecision(1, null); // 内部エラー → fail-open
            }

            await RespondAsync(server, decision.IsDeny ? 2 : 0, decision.Reason, decision.AdditionalContext)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            globalLog.Write(LogLevel.Error, $"接続処理失敗: {ex.Message}");
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ProjectContext GetOrCreateProject(string projectRoot)
    {
        var key = Path.GetFullPath(projectRoot);
        return Projects.GetOrAdd(key,
            r => new Lazy<ProjectContext>(() => ProjectContext.Create(_registry, _globalLog.Emit, r))).Value;
    }

    // ---- アイドル回収 ----

    private static async Task SweeperLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SweepInterval, ct).ConfigureAwait(false);
                var removedAny = SweepStaleProjects();
                // メモリ（プロジェクトキャッシュ）が全て空になったら daemon を自動停止する。
                // 起動直後（まだ 1 件も生成していない）の誤停止を避けるため、回収が発生した場合のみ判定。
                if (removedAny && Projects.IsEmpty)
                {
                    _globalLog.Write(LogLevel.Info, "全プロジェクト回収（メモリ空）につき daemon 終了。");
                    _shutdownCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 終了。
        }
    }

    /// <summary>
    /// 無アクセスが <see cref="ProjectEvictAfter"/> を超えたプロジェクトを回収する。
    /// 1 件以上回収したら true。
    /// </summary>
    private static bool SweepStaleProjects()
    {
        var threshold = DateTime.UtcNow - ProjectEvictAfter;
        var removedAny = false;
        foreach (var kv in Projects)
        {
            if (!kv.Value.IsValueCreated)
            {
                continue;
            }
            if (kv.Value.Value.LastAccessUtc < threshold
                && Projects.TryRemove(kv.Key, out var removed) && removed.IsValueCreated)
            {
                removed.Value.Dispose();
                removedAny = true;
                _globalLog.Write(LogLevel.Info, $"アイドルにつきプロジェクトを回収: {kv.Key}");
            }
        }
        return removedAny;
    }

    private static void DisposeAllProjects()
    {
        foreach (var kv in Projects)
        {
            if (kv.Value.IsValueCreated)
            {
                kv.Value.Value.Dispose();
            }
        }
        Projects.Clear();
    }

    // ---- 応答 ----

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task RespondAsync(
        NamedPipeServerStream server, int exitCode, string? reason, string? additionalContext = null)
    {
        var resp = new HookResponse
        {
            ExitCode = exitCode,
            Reason = reason,
            AdditionalContext = additionalContext,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(resp, ResponseJsonOptions);
        await Framing.WriteFrameAsync(server, json).ConfigureAwait(false);
    }

    // ---- ライフサイクル（bridge / CLI から呼ぶ） ----

    /// <summary>未起動なら daemon を detached 起動する。</summary>
    public static int Ensure()
    {
        if (IsRunning())
        {
            return 0;
        }
        StartDetached();
        return 0;
    }

    /// <summary>
    /// 稼働中の daemon を停止してから起動し直す。lib（プラグイン DLL）の差し替え反映用途。
    /// 停止したプロセスの終了（ロック解放）を待ってから起動し、多重起動ロックの競合を避ける。
    /// </summary>
    public static int Restart()
    {
        Stop();
        for (var i = 0; i < 30 && IsRunning(); i++)
        {
            Thread.Sleep(100);
        }
        StartDetached();
        return 0;
    }

    /// <summary>稼働中の daemon を停止させる。</summary>
    public static int Stop()
    {
        try
        {
            using var c = new NamedPipeClientStream(".", HarnessPipe.Name(), PipeDirection.InOut);
            c.Connect(1000);
            var env = new RequestEnvelope { Type = RequestEnvelope.TypeStop };
            var json = JsonSerializer.SerializeToUtf8Bytes(env, ResponseJsonOptions);
            Framing.WriteFrameAsync(c, json).GetAwaiter().GetResult();
            Framing.ReadFrameAsync(c).GetAwaiter().GetResult();
        }
        catch
        {
            // 既に停止していれば何もしない。
        }
        return 0;
    }

    public static bool IsRunning()
    {
        try
        {
            using var c = new NamedPipeClientStream(".", HarnessPipe.Name(), PipeDirection.InOut);
            c.Connect(500);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void StartDetached()
    {
        var exe = Environment.ProcessPath!; // ai-harness-main 自身
        var psi = new ProcessStartInfo(exe, "--daemon")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // hook の stdio を握らないよう切り離す（握ると Claude が hook 完了待ちでハングし得る）。
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Process.Start(psi);
    }
}
