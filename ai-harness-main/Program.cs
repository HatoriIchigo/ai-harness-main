using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// ai-harness-main エントリポイント。モードで分岐する。
///
///   （引数なし） … standalone。stdin の hook JSON を1件処理して終了（client 不要の直接実行・テスト用）。
///   --daemon     … 常駐。名前付きパイプで接続を待ち受け、接続ごとに処理。
///   --ensure     … daemon 未起動なら detached 起動して終了（SessionStart 等から）。
///   --stop       … 稼働中の daemon を停止。
///
/// 終了コード（standalone / Claude hook 規約）: 0=許可 / 2=deny / 1=内部エラー（非ブロッキング）。
/// 常駐時の hook 応答は client が中継する。ログは logs/&lt;日付&gt;.jsonl に集約。
/// </summary>
public static class Program
{
    private const int ExitAllow = 0;
    private const int ExitInternalError = 1;
    private const int ExitDeny = 2;

    public static async Task<int> Main(string[] args)
    {
        var config = HarnessConfig.Load(out var configWarning);
        var mode = args.Length > 0 ? args[0] : null;

        switch (mode)
        {
            case "--daemon":
            {
                // 常駐時は stderr に消費者がいないためファイルのみへ出力。
                var logger = new Logger(config.MinLogLevel, toStderr: false);
                if (configWarning is not null)
                {
                    logger.Write(LogLevel.Warning, configWarning);
                }
                return await Daemon.RunAsync(config, logger).ConfigureAwait(false);
            }

            case "--ensure":
                return Daemon.Ensure();

            case "--stop":
                return Daemon.Stop();

            default:
                return await RunStandaloneAsync(config, configWarning).ConfigureAwait(false);
        }
    }

    /// <summary>client を介さず stdin を直接1件処理する（テスト・フォールバック用）。</summary>
    private static async Task<int> RunStandaloneAsync(HarnessConfig config, string? configWarning)
    {
        var logger = new Logger(config.MinLogLevel);
        if (configWarning is not null)
        {
            logger.Write(LogLevel.Warning, configWarning);
        }

        HookData data;
        try
        {
            await using var stdin = Console.OpenStandardInput();
            data = await HookData.ParseAsync(stdin).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Write(LogLevel.Error, $"hook データの解析に失敗: {ex.Message}");
            return ExitInternalError;
        }

        logger.Write(LogLevel.Debug,
            $"config pluginDir={config.PluginDir} parallel={config.MaxParallel} logLevel={config.MinLogLevel}");
        logger.Write(LogLevel.Debug, data.ToNonNullJson());

        HostDecision decision;
        try
        {
            var core = new HarnessCore(logger, config.PluginDir, config.MaxParallel);
            decision = await core.RunAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Write(LogLevel.Error, $"処理に失敗: {ex.Message}");
            return ExitInternalError;
        }

        if (decision.IsDeny)
        {
            if (!string.IsNullOrWhiteSpace(decision.Reason))
            {
                Console.Error.WriteLine(decision.Reason);
            }
            return ExitDeny;
        }
        return ExitAllow;
    }
}
