namespace ai_harness_main;

/// <summary>
/// <c>--fire [プラグイン名]</c>: cwd から解決したプロジェクトに対し、有効プラグインの
/// 能動スキャン（<see cref="ai_harness_baselib.PluginBase.Fire"/>）を daemon 経由で起動し、結果を表示する。
/// プラグイン名を与えるとその 1 つだけを対象にする（未指定は全有効プラグイン）。
///
/// スキャンは daemon 内でプロジェクトを展開して走る（hook と同じ経路）。未起動なら detached 起動して待つ。
///
/// 終了コード: 0=問題なし（全プラグインが正常）/ 2=いずれかのプラグインが検出（非 0 を返した）/
/// 1=接続・実行不能。検出（2）は「差し戻し」ではなくスキャン結果の集約であり、CI 等で扱えるよう
/// コマンドの終了コードへ反映する。
/// </summary>
internal static class FireCommand
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitDetected = 2;

    /// <summary>daemon 起動後の再試行回数と間隔（bridge と同じ手順・猶予）。</summary>
    private const int RetryCount = 15;
    private const int RetryDelayMs = 200;

    public static async Task<int> RunAsync(string? pluginName)
    {
        var projectRoot = ProjectLocator.Resolve(Environment.CurrentDirectory);

        // --project/--logs 等の受動照会と違い、--fire は実際にスキャンを走らせる能動操作。
        // まず稼働中 daemon へ送信し、未起動なら detached 起動して再試行する（bridge と同じ手順）。
        // Fire はスキャン（副作用を持たない読み取り想定）ゆえ、再試行での再実行は許容する。
        var response = await DaemonClient.TryFireAsync(projectRoot, pluginName).ConfigureAwait(false);
        if (response is null)
        {
            Daemon.StartDetached();
            for (var i = 0; i < RetryCount && response is null; i++)
            {
                await Task.Delay(RetryDelayMs).ConfigureAwait(false);
                response = await DaemonClient.TryFireAsync(projectRoot, pluginName).ConfigureAwait(false);
            }
        }

        if (response is null)
        {
            await Console.Error.WriteLineAsync(
                "daemon に接続できませんでした（スキャンを実行できません）。").ConfigureAwait(false);
            return ExitError;
        }
        if (response.Error is not null)
        {
            await Console.Error.WriteLineAsync(response.Error).ConfigureAwait(false);
            return ExitError;
        }

        WriteReport(projectRoot, response.Plugins);
        // いずれかのプラグインが非 0（検出）なら 2。全て正常なら 0。
        return response.Plugins.Any(p => !p.IsOk) ? ExitDetected : ExitOk;
    }

    /// <summary>スキャン結果をプラグインごとのブロックで表示する。</summary>
    private static void WriteReport(string projectRoot, List<FirePluginResult> plugins)
    {
        var writer = Console.Out;
        writer.WriteLine($"project: {projectRoot}");

        if (plugins.Count == 0)
        {
            writer.WriteLine("（スキャンを実装した有効プラグインはありません）");
            return;
        }

        foreach (var p in plugins)
        {
            writer.WriteLine();
            writer.WriteLine($"{p.Name}  [{(p.IsOk ? "ok" : "detected")}]");
            if (!string.IsNullOrWhiteSpace(p.Reason))
            {
                writer.WriteLine($"  reason: {p.Reason}");
            }
            if (!string.IsNullOrWhiteSpace(p.AdditionalContext))
            {
                writer.WriteLine($"  context: {p.AdditionalContext}");
            }
            foreach (var line in p.Logs)
            {
                writer.WriteLine($"  {line}");
            }
        }
    }
}
