using System.Security.Cryptography;
using System.Text;

namespace ai_harness_main;

/// <summary>
/// 名前付きパイプ名を実行体ディレクトリから決定的に生成する。
/// daemon と client は同一ディレクトリに同居するため同じ名前になる（プロジェクト単位で分離）。
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
