using System.IO.Pipes;
using System.Text.Json;

namespace ai_harness_main;

/// <summary>
/// CLI（情報表示系サブコマンド）から稼働中の daemon へ問い合わせるクライアント。
/// bridge と違い daemon を<b>起動しない</b>（「いま動いているもの」を見るための照会であり、
/// 照会が daemon を生やしてしまうと結果が自己成就するため）。接続できなければ未起動とみなす。
/// </summary>
internal static class DaemonClient
{
    private const int ConnectTimeoutMs = 1000;

    /// <summary>メモリ上のプロジェクト一覧を問い合わせる。daemon 未起動・通信失敗は <c>null</c>。</summary>
    public static async Task<ProjectsResponse?> TryQueryProjectsAsync()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", HarnessPipe.Name(), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);

            var envelope = new RequestEnvelope { Type = RequestEnvelope.TypeProjects };
            await Framing.WriteFrameAsync(client, JsonSerializer.SerializeToUtf8Bytes(envelope))
                .ConfigureAwait(false);
            var payload = await Framing.ReadFrameAsync(client).ConfigureAwait(false);

            return JsonSerializer.Deserialize<ProjectsResponse>(payload);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// プロジェクトの能動スキャン（<c>--fire</c>）を daemon に依頼する。<paramref name="pluginName"/> 指定で
    /// 1 プラグインへ限定（null は全プラグイン）。接続・通信失敗は <c>null</c>。
    /// スキャンは時間がかかり得るため、接続確立にのみタイムアウトを課し、応答（実行完了）は待ち切る。
    /// </summary>
    public static async Task<FireResponse?> TryFireAsync(string projectRoot, string? pluginName)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", HarnessPipe.Name(), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);

            var envelope = new RequestEnvelope
            {
                Type = RequestEnvelope.TypeFire,
                ProjectRoot = projectRoot,
                PluginName = pluginName,
            };
            await Framing.WriteFrameAsync(client, JsonSerializer.SerializeToUtf8Bytes(envelope))
                .ConfigureAwait(false);
            var payload = await Framing.ReadFrameAsync(client).ConfigureAwait(false);

            return JsonSerializer.Deserialize<FireResponse>(payload);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// プロジェクトの LSP 稼働状況を問い合わせる（<c>--lsp &lt;プロジェクト&gt;</c>）。daemon 未起動・通信失敗は
    /// <c>null</c>。daemon がそのプロジェクトをメモリに展開していなければ（hook が一度も来ていない等）、
    /// 空の <see cref="LspStatusResponse"/> が返る（daemon はこの照会のために新規生成しない）。
    /// </summary>
    public static async Task<LspStatusResponse?> TryQueryLspAsync(string projectRoot)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", HarnessPipe.Name(), PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(ConnectTimeoutMs).ConfigureAwait(false);

            var envelope = new RequestEnvelope { Type = RequestEnvelope.TypeLsp, ProjectRoot = projectRoot };
            await Framing.WriteFrameAsync(client, JsonSerializer.SerializeToUtf8Bytes(envelope))
                .ConfigureAwait(false);
            var payload = await Framing.ReadFrameAsync(client).ConfigureAwait(false);

            return JsonSerializer.Deserialize<LspStatusResponse>(payload);
        }
        catch
        {
            return null;
        }
    }
}
