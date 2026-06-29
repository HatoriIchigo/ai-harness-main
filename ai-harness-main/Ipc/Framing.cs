namespace ai_harness_main;

/// <summary>
/// 長さ前置（int32 LE）フレーミング。bridge と daemon 間のパイプ送受で共通利用する。
/// </summary>
internal static class Framing
{
    private const int MaxFrame = 64 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream s, byte[] payload, CancellationToken ct = default)
    {
        await s.WriteAsync(BitConverter.GetBytes(payload.Length), ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream s, CancellationToken ct = default)
    {
        var lenBuf = await ReadExactAsync(s, 4, ct).ConfigureAwait(false);
        var len = BitConverter.ToInt32(lenBuf);
        if (len < 0 || len > MaxFrame)
        {
            throw new IOException($"frame 長が異常: {len}");
        }
        return await ReadExactAsync(s, len, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var off = 0;
        while (off < n)
        {
            var r = await s.ReadAsync(buf.AsMemory(off, n - off), ct).ConfigureAwait(false);
            if (r == 0)
            {
                throw new EndOfStreamException();
            }
            off += r;
        }
        return buf;
    }
}
