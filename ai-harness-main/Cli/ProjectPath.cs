namespace ai_harness_main;

/// <summary>
/// CLI の位置引数として渡されたプロジェクト（<c>C:\Users\project1</c> 形式）を絶対パスへ正規化する。
/// daemon 側のキーと同じく <see cref="Path.GetFullPath(string)"/> で揃える。
/// </summary>
internal static class ProjectPath
{
    /// <summary>存在するディレクトリなら絶対パスを返す。存在しなければ利用者向けの理由を返す。</summary>
    public static bool TryResolve(string project, out string root, out string error)
    {
        error = "";
        try
        {
            root = Path.GetFullPath(project);
        }
        catch (Exception ex)
        {
            root = "";
            error = $"プロジェクトのパスが不正です: {project}（{ex.Message}）";
            return false;
        }

        if (!Directory.Exists(root))
        {
            error = $"プロジェクトが存在しません: {root}";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 位置引数のプロジェクトを解決する。<paramref name="project"/> が <c>null</c>（無指定）なら
    /// cwd から上方探索でルートを決める（bridge と同じ探索）。
    /// </summary>
    public static bool TryResolveOrLocate(string? project, out string root, out string error)
    {
        if (project is not null)
        {
            return TryResolve(project, out root, out error);
        }
        root = ProjectLocator.Resolve(Environment.CurrentDirectory);
        error = "";
        return true;
    }
}
