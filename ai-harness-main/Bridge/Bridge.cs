using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ai_harness_main;

/// <summary>
/// 既定（橋渡し）モード。hook ごとに Claude Code から起動される薄い受け口。
/// stdin の hook JSON を読み、cwd からプロジェクトルートを解決して封筒に詰め、daemon（名前付きパイプ）へ送る。
/// daemon 未起動なら detached 起動して再接続。最終的に接続不能なら fail-open（exit 0）。
///
/// daemon は常駐ゆえ各 hook プロセスにはなれないため、この短命プロセスが構造的に必要
/// （hook spawn → 中継 → 応答 → 終了）。
///
/// hook 入力のガード（hook 経由の起動でなければ、中継せず使い方エラー＝exit 1 で即終了する）:
///   stdin が端末      … 人間が引数なしで直接叩いた形。読むと EOF が来ず入力待ちで固まるため、読まずに終了。
///   stdin が空        … hook JSON が渡ってきていない（<c>&lt; /dev/null</c> 等）。中継しても解析できない。
///   stdin がリダイレクトされているが読み切れない … サンドボックス経由の実行等、<see cref="Console.IsInputRedirected"/>
///     は <c>true</c> なのに相手側が EOF もデータも寄越さない環境がある（Claude Code の hook 経路では
///     必ず即座に書き切って閉じるため、正規の hook 呼び出しでは起きない）。読み取りに
///     <see cref="StdinReadTimeoutMs"/> の上限を設け、超えたら「叩き方の誤り」として同じ使い方エラーへ倒す
///     （タイムアウトが無いと無期限にブロックし、誤って直接実行した Claude 自身のプロセスごと固まる）。
/// hook として呼ばれた場合は必ずリダイレクトされた stdin に JSON が即座に載るため、この 3 つは hook 経路では起きない。
/// deny（2）ではなく 1 で終えるのは、ツールをブロックする判断ではなく<b>叩き方の誤り</b>だから
/// （Claude hook 規約でも 1 は非ブロッキングエラー）。
///
/// 応答の扱い:
///   deny（exitCode≠0） … 理由を stderr へ、その exit code で終了（2=ブロック）。
///   許可＋additionalContext … hook 出力 JSON（hookSpecificOutput.additionalContext）を stdout へ、exit 0。
///   許可のみ … 何も出さず exit 0。
/// </summary>
internal static class Bridge
{
    private const int ConnectTimeoutMs = 1500;
    private const int RetryCount = 15;
    private const int RetryDelayMs = 200;

    /// <summary>
    /// stdin の読み取りに許す上限。正規の hook 呼び出しは即座に書き切って閉じるため、この値は
    /// 「本物の hook 入力を待つ余裕」ではなく「入力が来ない誤った起動を無期限ブロックさせないための保険」。
    /// </summary>
    private const int StdinReadTimeoutMs = 2000;

    /// <summary>hook 経由の起動ではない（叩き方の誤り）。ブロック判断ではないので deny(2) とは別。</summary>
    private const int ExitUsage = 1;

    public static async Task<int> RunAsync()
    {
        // stdin が端末 = hook ではなく人間が直接起動した。読みにいくと EOF が来ず固まるため、読む前に返す。
        if (!Console.IsInputRedirected)
        {
            return await UsageErrorAsync(
                "hook データが渡されていません（引数なしは Claude Code の hook から stdin 経由で呼ばれる受け口です）。")
                .ConfigureAwait(false);
        }

        byte[] stdin;
        using (var ms = new MemoryStream())
        {
            // CancellationToken だけに頼らない: 環境によっては Console 標準入力の読み取りが
            // トークンを実質無視する（内部の同期読み取りがブロックしたまま戻らない）ことがあるため、
            // Task.WhenAny でタイムアウトと競走させる。タイムアウト側が勝ったら読み取りタスクの完了を
            // 待たずに使い方エラーへ抜ける（このプロセスはすぐ終了するので、放置されたタスクごと
            // プロセス終了時に破棄される＝リークの心配はない）。
            var readTask = Console.OpenStandardInput().CopyToAsync(ms);
            var winner = await Task.WhenAny(readTask, Task.Delay(StdinReadTimeoutMs)).ConfigureAwait(false);
            if (winner != readTask)
            {
                return await UsageErrorAsync(
                    "hook データが渡されていません（stdin からの入力が一定時間内に届きませんでした）。")
                    .ConfigureAwait(false);
            }
            await readTask.ConfigureAwait(false); // 例外があればここで観測する。
            stdin = ms.ToArray();
        }

        // 先頭 UTF-8 BOM を除去（Claude Code は付けないが、ラッパー経由で混入すると
        // U+FEFF が残り daemon 側の JSON パースが '0xEF invalid start of value' で失敗する）。
        if (stdin is [0xEF, 0xBB, 0xBF, ..])
        {
            stdin = stdin[3..];
        }

        var hookJson = Encoding.UTF8.GetString(stdin);

        // stdin は繋がっているが hook JSON が載っていない（`< /dev/null` 等）。中継しても解析できない。
        if (string.IsNullOrWhiteSpace(hookJson))
        {
            return await UsageErrorAsync(
                "hook データが空です（stdin から hook JSON を渡してください）。").ConfigureAwait(false);
        }

        var hookEventName = ExtractHookEventName(stdin);
        var projectRoot = ProjectLocator.Resolve(Environment.CurrentDirectory);

        var envelope = new RequestEnvelope
        {
            Type = RequestEnvelope.TypeHook,
            ProjectRoot = projectRoot,
            HookJson = hookJson,
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var pipeName = HarnessPipe.Name();

        // まず接続を試行。
        var result = await TrySendAsync(pipeName, payload).ConfigureAwait(false);
        if (result is { } first)
        {
            return await EmitAsync(first, hookEventName).ConfigureAwait(false);
        }

        // 未起動 → daemon を起動して再試行。
        Daemon.StartDetached();
        for (var i = 0; i < RetryCount; i++)
        {
            await Task.Delay(RetryDelayMs).ConfigureAwait(false);
            var retry = await TrySendAsync(pipeName, payload).ConfigureAwait(false);
            if (retry is { } r)
            {
                return await EmitAsync(r, hookEventName).ConfigureAwait(false);
            }
        }

        // 接続不能 → fail-open（ブロックしない）。
        return 0;
    }

    /// <summary>hook 経由の起動ではない旨と使い方を stderr へ出し、使い方エラー（1）で終える。</summary>
    private static async Task<int> UsageErrorAsync(string message)
    {
        Program.UseUtf8Output();
        await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
        await Console.Error.WriteLineAsync(Usage.Text).ConfigureAwait(false);
        return ExitUsage;
    }

    /// <summary>応答を Claude Code 向けに出力する。</summary>
    private static async Task<int> EmitAsync(HookResponse resp, string? hookEventName)
    {
        if (resp.ExitCode != 0)
        {
            if (!string.IsNullOrEmpty(resp.Reason))
            {
                await Console.Error.WriteAsync(resp.Reason).ConfigureAwait(false);
            }
            return resp.ExitCode;
        }

        if (!string.IsNullOrEmpty(resp.AdditionalContext) && !string.IsNullOrEmpty(hookEventName))
        {
            await Console.Out.WriteAsync(
                HookOutput.BuildAdditionalContext(hookEventName, resp.AdditionalContext)).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<HookResponse?> TrySendAsync(string pipeName, byte[] payload)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);

            await Framing.WriteFrameAsync(client, payload).ConfigureAwait(false);
            var resp = await Framing.ReadFrameAsync(client).ConfigureAwait(false);

            return JsonSerializer.Deserialize<HookResponse>(resp);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>stdin の hook JSON から hook_event_name を取り出す（不正・不在は null）。</summary>
    private static string? ExtractHookEventName(byte[] stdin)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdin);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("hook_event_name", out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        catch
        {
            // パース不能なら null（context 注入はスキップされる）。
        }
        return null;
    }
}
