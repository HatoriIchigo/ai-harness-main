using System.Security.Cryptography;
using System.Text;

namespace ai_harness_main;

/// <summary>
/// 名前付きパイプ名を実行体ディレクトリから決定的に生成する。
/// 単一バイナリの bridge モードと daemon モードは同一実行体（同一 BaseDirectory）から起動されるため
/// 同じ名前を算出する。インストール単位で 1 つの共有 daemon に対応（複数インストールの衝突は実行体ディレクトリで回避）。
/// </summary>
internal static class HarnessPipe
{
    public static string Name()
    {
        var key = AppContext.BaseDirectory;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return $"ai-harness-{hash[..16]}";
    }
}
