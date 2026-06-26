using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 常駐モード。起動時にプラグイン型を1回発見し、名前付きパイプで接続を待ち受け、
/// 接続ごとに hook データを処理する。client が hook ごとに接続してくる。
/// </summary>
internal static class Daemon
{
    private const string StopMagic = "__AI_HARNESS_STOP__";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>daemon 本体。多重起動はロックファイルで抑止。</summary>
    public static async Task<int> RunAsync(HarnessConfig config, Logger logger)
    {
        // 多重起動防止（best-effort）。FileShare.None で2つ目はオープン失敗。
        FileStream lockFile;
        var lockPath = Path.Combine(config.RunDir, "daemon.lock");
        try
        {
            Directory.CreateDirectory(config.RunDir);
            lockFile = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }
        catch
        {
            logger.Write(LogLevel.Info, "別の daemon が稼働中。起動を中止。");
            return 0;
        }

        try
        {
            var core = new HarnessCore(logger, config.PluginDir, config.MaxParallel);
            var pipeName = HarnessPipe.Name();
            logger.Write(LogLevel.Info, $"daemon 起動 pipe={pipeName} plugins={core.PluginCount}");

            while (true)
            {
                var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                using var idleCts = new CancellationTokenSource(IdleTimeout);
                try
                {
                    await server.WaitForConnectionAsync(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    logger.Write(LogLevel.Info, "アイドルタイムアウトで daemon 終了。");
                    return 0;
                }

                // 接続は並行処理（次の接続受付を妨げない）。
                _ = HandleAsync(server, core, logger);
            }
        }
        finally
        {
            lockFile.Dispose();
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream server, HarnessCore core, Logger logger)
    {
        try
        {
            var payload = await Framing.ReadFrameAsync(server).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(payload);

            if (text == StopMagic)
            {
                await RespondAsync(server, 0, "stopping").ConfigureAwait(false);
                logger.Write(LogLevel.Info, "stop 要求で daemon 終了。");
                Environment.Exit(0);
            }

            HostDecision decision;
            try
            {
                var data = HookData.Parse(text);
                decision = await core.RunAsync(data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Write(LogLevel.Error, $"hook 処理失敗: {ex.Message}");
                decision = new HostDecision(1, null); // 内部エラー → fail-open
            }

            // client へは 2=deny / それ以外=許可（fail-open）。
            await RespondAsync(server, decision.IsDeny ? 2 : 0, decision.Reason ?? string.Empty)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Write(LogLevel.Error, $"接続処理失敗: {ex.Message}");
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RespondAsync(NamedPipeServerStream server, int exitCode, string reason)
    {
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
        var buf = new byte[4 + reasonBytes.Length];
        BitConverter.GetBytes(exitCode).CopyTo(buf, 0);
        reasonBytes.CopyTo(buf, 4);
        await Framing.WriteFrameAsync(server, buf).ConfigureAwait(false);
    }

    /// <summary>未起動なら daemon を detached 起動する（SessionStart 等から呼ぶ）。</summary>
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
    /// 稼働中の daemon を停止してから起動し直す。プラグイン DLL や config の変更を反映する用途。
    /// 旧プロセスの終了（パイプ消滅＝ロック解放）を待ってから起動し、多重起動ロックの競合を避ける。
    /// </summary>
    public static int Restart()
    {
        Stop();
        // 旧プロセスの終了をポーリングで待つ（best-effort、上限約3秒）。
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
            Framing.WriteFrameAsync(c, Encoding.UTF8.GetBytes(StopMagic)).GetAwaiter().GetResult();
            Framing.ReadFrameAsync(c).GetAwaiter().GetResult();
        }
        catch
        {
            // 既に停止していれば何もしない。
        }
        return 0;
    }

    private static bool IsRunning()
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

    private static void StartDetached()
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
