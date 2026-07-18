using System.Text.Json.Nodes;

namespace ai_harness_main;

/// <summary>アーカイブの展開方式。ダウンロードした 1 ファイルをどう扱うかを表す（<see cref="InstallKind.Download"/> 用）。</summary>
internal enum ArchiveKind
{
    /// <summary>
    /// zip 展開（複数ファイル）。展開直後の内容がバージョン名入りの単一ディレクトリ 1 個だけなら、
    /// その中身を 1 段上へ引き上げてから配置する（<see cref="LspManager"/> 側の処理。
    /// <see cref="LspRidBinary.ExecutableRelativePath"/> をバージョンに依存しない固定値にできる）。
    /// </summary>
    Zip,

    /// <summary>gzip 展開（単一バイナリの圧縮のみ、tar は伴わない）。</summary>
    GZip,

    /// <summary>tar+gzip 展開（複数ファイル。jdtls 等）。</summary>
    TarGZip,
}

/// <summary>インストール方式。言語の配布形態そのものが違うため、ダウンロード以外も扱う。</summary>
internal enum InstallKind
{
    /// <summary>アーカイブ／単体バイナリを直接ダウンロードして展開する（rust／cpp／java）。</summary>
    Download,

    /// <summary><c>npm install --prefix &lt;installDir&gt;</c>（typescript／pyright）。<c>node</c>／<c>npm</c> が PATH 前提。</summary>
    NpmInstall,

    /// <summary><c>go install &lt;module&gt;@latest</c>（<c>GOBIN</c> を installDir/bin へ上書き）（go）。<c>go</c> が PATH 前提。</summary>
    GoInstall,

    /// <summary>
    /// <c>python -m venv &lt;installDir&gt;</c> の上に <c>pip install</c>（pylsp／jedi-language-server）。
    /// <c>python</c> が PATH 前提。venv 化するのはグローバル site-packages を汚さないため。
    /// </summary>
    PipInstall,

    /// <summary>
    /// <c>dotnet tool install --tool-path &lt;installDir&gt;</c>（csharp-ls）。<c>dotnet</c> が PATH 前提。
    /// ai-harness 自身が .NET プロジェクトなので、このホストで LSP を使う分には既に満たされている前提が多い。
    /// </summary>
    DotnetToolInstall,
}

/// <summary>起動方式。インストール後、実際にどう子プロセスを立てるか。</summary>
internal enum LaunchKind
{
    /// <summary>
    /// 解決した実行ファイルをそのまま起動する（rust／cpp／go＝go install 後のネイティブバイナリ／
    /// pylsp・jedi＝venv 内の console_script）。
    /// </summary>
    Direct,

    /// <summary>PATH の <c>node</c> を起動し、第1引数に installDir 配下の JS エントリを渡す（pyright／typescript）。</summary>
    Node,

    /// <summary>
    /// PATH の <c>java</c> を起動する（java＝jdtls）。launcher jar はファイル名にビルド日時が入るため
    /// <see cref="LspDefinition.JavaLauncherJarGlob"/> で展開後に glob 解決し、<c>-configuration</c> は
    /// RID ごとに異なるディレクトリ名（<see cref="LspDefinition.JavaConfigDirByRid"/>）、
    /// <c>-data</c> はプロジェクトごとに専用のワークスペースディレクトリを都度用意して渡す。
    /// </summary>
    Java,
}

/// <summary>
/// GitHub の最新リリースから asset を解決するダウンロード元。ファイル名にバージョン番号が入るため
/// （例: <c>clangd-windows-22.1.6.zip</c>）<c>releases/latest/download/&lt;固定名&gt;</c> が使えない言語向け。
/// <c>lsp.yml</c> には書かない（解決自体を固定ロジックとして持つ。ユーザーが変えられる「ダウンロード URL」
/// という性質のものではないため）。
/// </summary>
internal sealed record GitHubAssetResolver(string Repo, string AssetNamePattern);

/// <summary>
/// ある言語・ある RID の 1 ダウンロードから、実行ファイルの配置場所を導く定義（<see cref="InstallKind.Download"/> 用）。
/// <see cref="ExecutableRelativePath"/> は展開後ディレクトリからの相対パス（<see cref="ArchiveKind.GZip"/> は
/// 展開でこの相対パスへ直接書き出す＝実質ファイル名そのもの）。
/// <see cref="Resolver"/> が非 null の言語は <c>lsp.yml</c> の URL を見ず、GitHub の最新リリースから
/// 都度 asset を解決する。
/// </summary>
internal sealed record LspRidBinary(
    ArchiveKind Archive, string ExecutableRelativePath, GitHubAssetResolver? Resolver = null);

/// <summary>
/// 1 サーバぶんの LSP 定義。実行コマンド・引数はここで固定（ダウンロード元だけが可変値）。
/// フィールドの意味は <see cref="Install"/>／<see cref="Launch"/> の組み合わせで変わる（使うものだけ埋める）。
/// </summary>
internal sealed record LspDefinition(
    string Language,
    InstallKind Install,
    LaunchKind Launch,
    IReadOnlyList<string> Args,
    // InstallKind.Download 用。
    IReadOnlyDictionary<string, LspRidBinary>? Binaries = null,
    // InstallKind.NpmInstall 用。npm install に渡すパッケージ名（複数可。peer dependency も含める）。
    IReadOnlyList<string>? NpmPackages = null,
    // InstallKind.GoInstall 用。`go install <GoModule>@latest` のモジュールパス。
    string? GoModule = null,
    // InstallKind.PipInstall 用。pip install に渡すパッケージ名。
    IReadOnlyList<string>? PipPackages = null,
    // InstallKind.PipInstall 用。venv の console_script 名（拡張子・Scripts/bin は LspManager が OS ごとに解決）。
    string? PipEntryPoint = null,
    // InstallKind.DotnetToolInstall 用。`dotnet tool install --tool-path <installDir> <package>` のパッケージ名。
    // 生成される実行ファイル名は既定でパッケージ名と一致するため、別途エントリ名は持たない。
    string? DotnetToolPackage = null,
    // LaunchKind.Node 用。node へ渡すエントリスクリプトの installDir 相対パス。
    string? NodeEntryRelativePath = null,
    // LaunchKind.Java 用。展開後ディレクトリからの glob（例: "plugins/org.eclipse.equinox.launcher_*.jar"）。
    string? JavaLauncherJarGlob = null,
    // LaunchKind.Java 用。RID → -configuration に渡すディレクトリ名（例: win-x64 → "config_win"）。
    IReadOnlyDictionary<string, string>? JavaConfigDirByRid = null,
    // initialize ハンドシェイク後に workspace/didChangeConfiguration の settings として送る値（任意）。
    // サーバの診断の厳しさ（例: pyright の未使用シンボル報告）を、プロジェクト側の設定ファイル無しで
    // 有効化するために使う。null なら何も送らない（サーバの既定設定のまま）。
    JsonNode? InitializationSettings = null);

/// <summary>
/// 1 言語ぶんの候補サーバ一覧。<see cref="Servers"/> のキーが <c>lsp.yml</c> の <c>servers.&lt;言語&gt;</c> で
/// 選べる値。未選択（<c>lsp.yml</c> に無い）なら <see cref="DefaultServer"/> を使う。
/// 複数候補を持たせる言語（python 等）でも、選べるのはここに列挙した固定の候補だけ
/// （ユーザーが任意のコマンドを指定することはできない）。
/// </summary>
internal sealed record LspLanguageCatalog(string DefaultServer, IReadOnlyDictionary<string, LspDefinition> Servers);

/// <summary>
/// 対応言語 → 候補サーバ一覧の固定テーブル。「どの LSP を使えるか」「どう入れてどう起動するか」はここで固定し、
/// ユーザーが自由なコマンドを指定することはできない。<c>lsp.yml</c> はダウンロード URL
/// （<see cref="InstallKind.Download"/> かつ <see cref="GitHubAssetResolver"/> を使わないサーバのみ）と、
/// 複数候補がある言語のサーバ選択（<c>servers:</c>）だけを持つ。
///
/// <c>java</c>（jdtls）は JVM、<c>pyright</c>／<c>typescript-language-server</c> は Node.js、<c>go</c>（gopls）は
/// Go ツールチェーン、<c>pylsp</c>／<c>jedi-language-server</c> は Python、<c>csharp-ls</c> は dotnet SDK が
/// 実行環境の PATH に入っている前提（LSP 自体の配布が単体バイナリではなく、各言語のパッケージマネージャ／
/// ビルド経由のため）。<c>rust</c>／<c>cpp</c> だけは追加のランタイム前提が無い（単体バイナリをダウンロードするだけ）。
///
/// <see cref="Languages"/> に無い言語は <c>common.yml</c> の <c>lsp:</c> に書いても起動されない。
/// </summary>
internal static class LspCatalog
{
    public static IReadOnlyDictionary<string, LspLanguageCatalog> Languages { get; } =
        new Dictionary<string, LspLanguageCatalog>(StringComparer.Ordinal)
        {
            ["rust"] = new LspLanguageCatalog("rust-analyzer", Solo(new LspDefinition(
                Language: "rust",
                Install: InstallKind.Download,
                Launch: LaunchKind.Direct,
                Args: [],
                Binaries: new Dictionary<string, LspRidBinary>(StringComparer.Ordinal)
                {
                    ["win-x64"] = new LspRidBinary(ArchiveKind.Zip, "rust-analyzer.exe"),
                    ["linux-x64"] = new LspRidBinary(ArchiveKind.GZip, "rust-analyzer"),
                }), "rust-analyzer")),

            // C/C++ 共通（clangd が両方を扱う）。asset 名にバージョンが入るため GitHubAssetResolver で解決する。
            ["cpp"] = new LspLanguageCatalog("clangd", Solo(new LspDefinition(
                Language: "cpp",
                Install: InstallKind.Download,
                Launch: LaunchKind.Direct,
                Args: [],
                Binaries: new Dictionary<string, LspRidBinary>(StringComparer.Ordinal)
                {
                    ["win-x64"] = new LspRidBinary(
                        ArchiveKind.Zip, "bin/clangd.exe",
                        new GitHubAssetResolver("clangd/clangd", "clangd-windows-*.zip")),
                    ["linux-x64"] = new LspRidBinary(
                        ArchiveKind.Zip, "bin/clangd",
                        new GitHubAssetResolver("clangd/clangd", "clangd-linux-*.zip")),
                }), "clangd")),

            // python: 既定は pyright（型チェック重視・要 Node.js）。pylsp／jedi は要 Python のみ（Node.js 不要）で
            // 精度は控えめ。lsp.yml の servers.python で切り替える。
            ["python"] = new LspLanguageCatalog("pyright", new Dictionary<string, LspDefinition>(StringComparer.Ordinal)
            {
                // npm 配布のみ（単体バイナリ無し）。node で dist エントリを直接起動する
                // （node_modules/.bin の shim 経由ではなく、package.json の bin マッピング先を直接指定）。
                ["pyright"] = new LspDefinition(
                    Language: "python",
                    Install: InstallKind.NpmInstall,
                    Launch: LaunchKind.Node,
                    Args: ["--stdio"],
                    NpmPackages: ["pyright"],
                    NodeEntryRelativePath: "node_modules/pyright/langserver.index.js",
                    // pyright は既定（basic モード）だと未使用 import/変数/関数/クラスを一切報告しない
                    // （実測で確認済み）。プロジェクト側に pyrightconfig.json 等を用意させずに使えるよう、
                    // ここで明示的に有効化する。
                    InitializationSettings: new JsonObject
                    {
                        ["python"] = new JsonObject
                        {
                            ["analysis"] = new JsonObject
                            {
                                ["diagnosticSeverityOverrides"] = new JsonObject
                                {
                                    ["reportUnusedImport"] = "warning",
                                    ["reportUnusedVariable"] = "warning",
                                    ["reportUnusedClass"] = "warning",
                                    ["reportUnusedFunction"] = "warning",
                                },
                            },
                        },
                    }),

                ["pylsp"] = new LspDefinition(
                    Language: "python",
                    Install: InstallKind.PipInstall,
                    Launch: LaunchKind.Direct,
                    Args: [],
                    PipPackages: ["python-lsp-server"],
                    PipEntryPoint: "pylsp"),

                ["jedi"] = new LspDefinition(
                    Language: "python",
                    Install: InstallKind.PipInstall,
                    Launch: LaunchKind.Direct,
                    Args: [],
                    PipPackages: ["jedi-language-server"],
                    PipEntryPoint: "jedi-language-server"),
            }),

            // typescript-language-server。npm 配布のみ。typescript 本体を peer dependency として同時 install する。
            ["typescript"] = new LspLanguageCatalog(
                "typescript-language-server", Solo(new LspDefinition(
                Language: "typescript",
                Install: InstallKind.NpmInstall,
                Launch: LaunchKind.Node,
                Args: ["--stdio"],
                NpmPackages: ["typescript-language-server", "typescript"],
                NodeEntryRelativePath: "node_modules/typescript-language-server/lib/cli.mjs"),
                "typescript-language-server")),

            // csharp: 既定（唯一の候補）は csharp-ls（Roslynベースの軽量LSP、dotnet tool 配布）。
            // OmniSharp も検討したが、配布物を実際に展開して確認したところ Windows 版は無印＝.NET Framework 系
            // （refs/mscorlib.dll を同梱）、-net6.0 版＝.NET 6 ランタイム前提（hostfxr 等の同梱無し＝
            // フレームワーク依存）で、どちらも前提が重い／古いため候補から外した。servers.csharp で
            // 切り替えられる形自体は python と同じ器（Servers）を使っており、良い候補が見つかれば追加できる。
            ["csharp"] = new LspLanguageCatalog("csharp-ls", Solo(new LspDefinition(
                Language: "csharp",
                Install: InstallKind.DotnetToolInstall,
                Launch: LaunchKind.Direct,
                Args: [],
                DotnetToolPackage: "csharp-ls"), "csharp-ls")),

            // gopls。prebuilt バイナリ配布が無く `go install` 前提。GOBIN を installDir/bin へ向けてビルドさせる
            // （LspManager 側）。ビルド後は go install した実行ファイルをそのまま起動する（Direct）。
            ["go"] = new LspLanguageCatalog("gopls", Solo(new LspDefinition(
                Language: "go",
                Install: InstallKind.GoInstall,
                Launch: LaunchKind.Direct,
                Args: [],
                GoModule: "golang.org/x/tools/gopls"), "gopls")),

            // jdtls。単一 tar.gz が全 RID 共通（中身は jar のみでネイティブ差異が無い）。要 JVM21+（実測で確認済み）。
            // launcher jar はファイル名にビルド日時が入るため展開後に glob 解決する。
            // 起動引数は公式 README の推奨値（-jar／-configuration／-data は LspManager が動的に付与）。
            ["java"] = new LspLanguageCatalog("jdtls", Solo(new LspDefinition(
                Language: "java",
                Install: InstallKind.Download,
                Launch: LaunchKind.Java,
                Args:
                [
                    "-Declipse.application=org.eclipse.jdt.ls.core.id1",
                    "-Dosgi.bundles.defaultStartLevel=4",
                    "-Declipse.product=org.eclipse.jdt.ls.core.product",
                    "-Dlog.level=ALL",
                    "-Xmx1G",
                    "--add-modules=ALL-SYSTEM",
                    "--add-opens", "java.base/java.util=ALL-UNNAMED",
                    "--add-opens", "java.base/java.lang=ALL-UNNAMED",
                ],
                Binaries: new Dictionary<string, LspRidBinary>(StringComparer.Ordinal)
                {
                    // ExecutableRelativePath は使わない（Launch=Java は起動可否を launcher jar の有無で判定する）が、
                    // LspRidBinary は Archive・Resolver 情報の器として共有する。
                    ["win-x64"] = new LspRidBinary(ArchiveKind.TarGZip, "plugins"),
                    ["linux-x64"] = new LspRidBinary(ArchiveKind.TarGZip, "plugins"),
                },
                JavaLauncherJarGlob: "plugins/org.eclipse.equinox.launcher_*.jar",
                JavaConfigDirByRid: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["win-x64"] = "config_win",
                    ["linux-x64"] = "config_linux",
                }), "jdtls")),
        };

    /// <summary>候補が 1 つしかない言語向けの <see cref="LspLanguageCatalog.Servers"/> 生成ヘルパ。</summary>
    private static IReadOnlyDictionary<string, LspDefinition> Solo(LspDefinition definition, string serverName) =>
        new Dictionary<string, LspDefinition>(StringComparer.Ordinal) { [serverName] = definition };
}
