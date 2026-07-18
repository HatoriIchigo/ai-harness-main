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

    /// <summary>
    /// プロジェクトへコピーする既定リソース（静的ファイル）の置き場（実行体隣の <c>resources/</c>）。
    /// 例: <c>phase.yml</c>（プロジェクトに phase.yml が無いとき本ディレクトリからコピーする）。
    /// </summary>
    public static string ResourcesDir => Path.Combine(AppContext.BaseDirectory, "resources");

    /// <summary>daemon の作業領域（実行体隣の <c>run/</c>。ロックファイル等）。グローバル単一。</summary>
    public static string RunDir => Path.Combine(AppContext.BaseDirectory, "run");

    /// <summary>
    /// 本体（実行体）直下の設定ディレクトリ（実行体隣の <c>config/</c>）。全プロジェクト共通。
    /// プラグインのインストール定義 <c>plugins.yml</c> を置く（プロジェクト個別設定とは別系統）。
    /// </summary>
    public static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "config");

    /// <summary>
    /// <c>--update</c> がプラグインを clone／build する作業領域（実行体隣の <c>repos/</c>）。グローバル単一。
    /// </summary>
    public static string ReposDir => Path.Combine(AppContext.BaseDirectory, "repos");

    /// <summary>プラグインインストール定義ファイル（<c>&lt;実行体&gt;/config/plugins.yml</c>）。</summary>
    public static string PluginsConfigPath => Path.Combine(ConfigDir, "plugins.yml");

    /// <summary>
    /// LSP サーバの自動インストール先（実行体隣の <c>lsp/&lt;言語&gt;/</c>）。グローバル単一
    /// （プロジェクトごとに分けない。同一言語は全プロジェクトで同じ展開物を共有する）。
    /// </summary>
    public static string LspDir => Path.Combine(AppContext.BaseDirectory, "lsp");

    /// <summary>LSP ダウンロード元定義ファイル（<c>&lt;実行体&gt;/config/lsp.yml</c>）。不在なら起動時に既定値で自動作成する。</summary>
    public static string LspConfigPath => Path.Combine(ConfigDir, "lsp.yml");

    /// <summary>
    /// daemon 自身のライフサイクルログ出力先（実行体隣の <c>logs/</c>）。
    /// プロジェクト解決前のイベント（型発見・起動・回収・停止）を記録する。
    /// プロジェクト解決後の hook 処理ログは各プロジェクトの <see cref="ProjectConfig.LogDir"/> へ。
    /// </summary>
    public static string GlobalLogDir => Path.Combine(AppContext.BaseDirectory, "logs");
}
