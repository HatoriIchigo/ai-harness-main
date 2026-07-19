namespace ai_harness_main;

/// <summary>
/// プロセスの cwd からプロジェクトルートを解決する。
/// ルートは <c>.claude</c> ディレクトリを含む階層。cwd から上方へ <c>.claude</c> を探索する
/// （サブディレクトリ cwd でも、settings.json が <c>.claude</c> 内にある構成でも、その親を正しく取る）。
/// 見つからなければ cwd 自体をフォールバックとして返す。
///
/// ユーザーのホームディレクトリは上方探索で暗黙に選ばない。<c>~/.claude</c> は Claude Code の
/// **グローバル**設定（全プロジェクト共通の <c>settings.json</c> 等）であり、プロジェクト個別の
/// <c>.claude</c> とは別物。ホームディレクトリ配下の、まだ配線されていないサブディレクトリ（例:
/// プロジェクトでも何でもない作業フォルダ）で実行すると、上方探索が途中でホームの <c>.claude</c> に
/// 突き当たり「プロジェクトルートはホームディレクトリ」と誤認する。特に <c>--init</c> はここへ
/// <c>settings.json</c> を書き込むため、誤認するとユーザーの**グローバル**設定を汚染してしまう。
/// ホームに達したら（そこに <c>.claude</c> があっても）「見つからなかった」扱いで探索を打ち切る。
/// </summary>
internal static class ProjectLocator
{
    public static string Resolve(string startDir)
    {
        var full = Path.GetFullPath(startDir);
        var home = GetHomeDirectory();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var dir = new DirectoryInfo(full);
        while (dir is not null)
        {
            if (home is not null && string.Equals(dir.FullName, home, comparison))
            {
                break;
            }
            if (Directory.Exists(Path.Combine(dir.FullName, ".claude")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return full;
    }

    /// <summary>ユーザーのホームディレクトリ（絶対パス）。取得できなければ <c>null</c>（境界判定を無効化）。</summary>
    private static string? GetHomeDirectory()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : Path.GetFullPath(home);
        }
        catch
        {
            return null;
        }
    }
}
