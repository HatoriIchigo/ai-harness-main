namespace ai_harness_main;

/// <summary>
/// プロセスの cwd からプロジェクトルートを解決する。
/// ルートは <c>.claude</c> ディレクトリを含む階層。cwd から上方へ <c>.claude</c> を探索する
/// （サブディレクトリ cwd でも、settings.json が <c>.claude</c> 内にある構成でも、その親を正しく取る）。
/// 見つからなければ cwd 自体をフォールバックとして返す。
/// </summary>
internal static class ProjectLocator
{
    public static string Resolve(string startDir)
    {
        var full = Path.GetFullPath(startDir);
        var dir = new DirectoryInfo(full);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".claude")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return full;
    }
}
