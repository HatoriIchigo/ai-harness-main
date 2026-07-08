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
    public static string Name() => NameFor(AppContext.BaseDirectory);

    /// <summary>
    /// 指定した実行体ディレクトリ基準でパイプ名を算出する。自己更新の applier は tmp の新バイナリから動くため、
    /// インストール先（置換対象 exe のディレクトリ）基準で稼働中 daemon のパイプ名を求めるのに使う。
    /// </summary>
    public static string NameFor(string baseDir)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseDir)));
        return $"ai-harness-{hash[..16]}";
    }
}
