using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// ai-harness-main エントリポイント（単一バイナリ）。モードで分岐する。
///
///   （引数なし） … bridge。hook ごとに Claude Code が叩く受け口。stdin の hook JSON を読み、cwd から
///                  プロジェクトルートを解決し、封筒で daemon へ中継する。未起動なら daemon を起動。
///   --daemon     … 常駐サーバー。名前付きパイプで待ち受け、プロジェクト別に処理。複数プロジェクト共有。
///   --ensure     … daemon 未起動なら detached 起動して終了。
///   --stop       … 稼働中の daemon を停止。
///   --restart    … daemon を停止してから再起動（lib＝プラグイン DLL の差し替え反映用）。
///   --standalone … daemon を介さず stdin を直接 1 件処理して終了（テスト・フォールバック）。
///
/// 終了コード（bridge / standalone の Claude hook 規約）: 0=許可 / 2=deny / 1=内部エラー（非ブロッキング）。
/// </summary>
public static class Program
{
    private const int ExitAllow = 0;
    private const int ExitInternalError = 1;
    private const int ExitDeny = 2;

    public static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : null;

        switch (mode)
        {
            case "--daemon":
            {
                // 常駐時は stderr に消費者がいないためファイルのみへ出力（グローバル log）。
                var logger = new Logger(LogLevel.Info, InstallPaths.GlobalLogDir, toStderr: false);
                return await Daemon.RunAsync(logger).ConfigureAwait(false);
            }

            case "--ensure":
                return Daemon.Ensure();

            case "--stop":
                return Daemon.Stop();

            case "--restart":
                return Daemon.Restart();

            case "--standalone":
                return await RunStandaloneAsync().ConfigureAwait(false);

            default:
                return await Bridge.RunAsync().ConfigureAwait(false);
        }
    }

    /// <summary>daemon を介さず stdin を直接 1 件処理する（テスト・フォールバック用）。</summary>
    private static async Task<int> RunStandaloneAsync()
    {
        var projectRoot = ProjectLocator.Resolve(Environment.CurrentDirectory);

        HookData data;
        try
        {
            await using var stdin = Console.OpenStandardInput();
            data = await HookData.ParseAsync(stdin).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"hook データの解析に失敗: {ex.Message}").ConfigureAwait(false);
            return ExitInternalError;
        }

        HostDecision decision;
        ProjectContext? ctx = null;
        try
        {
            var registry = new PluginRegistry(_ => { }, InstallPaths.LibDir);
            ctx = ProjectContext.Create(registry, _ => { }, projectRoot);
            decision = await ctx.RunAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"処理に失敗: {ex.Message}").ConfigureAwait(false);
            return ExitInternalError;
        }
        finally
        {
            ctx?.Dispose();
        }

        if (decision.IsDeny)
        {
            if (!string.IsNullOrWhiteSpace(decision.Reason))
            {
                await Console.Error.WriteLineAsync(decision.Reason).ConfigureAwait(false);
            }
            return ExitDeny;
        }
        // 非ブロックのコンテキスト注入。standalone は自身で hook 出力 JSON を stdout へ。
        if (!string.IsNullOrEmpty(decision.AdditionalContext) && !string.IsNullOrEmpty(data.HookEventName))
        {
            Console.Out.Write(HookOutput.BuildAdditionalContext(data.HookEventName, decision.AdditionalContext));
        }
        return ExitAllow;
    }
}
