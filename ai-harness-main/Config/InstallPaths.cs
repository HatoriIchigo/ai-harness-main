namespace ai_harness_main;

/// <summary>
/// 実行体（インストールディレクトリ）基準で固定のグローバルパス。
/// 単一 daemon・共有プラグインに属するもの（lib・run・ライフサイクル log）を保持する。
/// プロジェクト個別の config/log は <see cref="ProjectConfig"/> 側で解決する。
///
/// プログラムは自前の環境変数を持たない。PATH 解決（Windows=PATH / Linux=/usr/local/bin への symlink）で
/// 起動され、ここでの基準は <see cref="AppContext.BaseDirectory"/>（実行体の実ディレクトリ）。
/// </summary>
internal static class InstallPaths
{
    /// <summary>プロジェクトルート直下のハーネス用サブディレクトリ。</summary>
    public const string HarnessSubdir = ".claude/harness";

    /// <summary>共有プラグイン DLL の走査先（実行体隣の <c>lib/</c>）。全プロジェクト共通。</summary>
    public static string LibDir => Path.Combine(AppContext.BaseDirectory, "lib");

    /// <summary>daemon の作業領域（実行体隣の <c>run/</c>。ロックファイル等）。グローバル単一。</summary>
    public static string RunDir => Path.Combine(AppContext.BaseDirectory, "run");

    /// <summary>
    /// daemon 自身のライフサイクルログ出力先（実行体隣の <c>logs/</c>）。
    /// プロジェクト解決前のイベント（型発見・起動・回収・停止）を記録する。
    /// プロジェクト解決後の hook 処理ログは各プロジェクトの <see cref="ProjectConfig.LogDir"/> へ。
    /// </summary>
    public static string GlobalLogDir => Path.Combine(AppContext.BaseDirectory, "logs");
}
