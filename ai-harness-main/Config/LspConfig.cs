using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ai_harness_main;

/// <summary>
/// LSP サーバのダウンロード元定義とサーバ選択。実行体隣の <c>config/lsp.yml</c> からロードする。
///
/// <see cref="DaemonConfig"/>／<c>plugins.yml</c> と同列のグローバル設定（プロジェクト個別ではない）だが、
/// それらと異なり<b>不在時は既定テンプレートを書き出す</b>（<see cref="Load"/> 参照）。
/// インストール先パス（<see cref="InstallPaths.LspDir"/>）や実行コマンド・引数（<see cref="LspCatalog"/>）は
/// ここには書かない。ここに書くのは言語ごと・RID ごとのダウンロード URL と、複数候補がある言語のサーバ選択のみ。
///
/// 何も強制しない実行時パラメータのため、ファイル不在・破損・不正値は<b>既定値（空）で継続</b>する
/// （<see cref="DaemonConfig"/> と同じ方針）。
/// </summary>
internal sealed class LspConfig
{
    /// <summary>言語 → RID → ダウンロード URL。</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Languages { get; init; }

    /// <summary>
    /// 言語 → 選択したサーバ名（<see cref="LspCatalog.LspLanguageCatalog.Servers"/> のキー）。
    /// 未指定の言語は <see cref="LspCatalog.LspLanguageCatalog.DefaultServer"/> を使う（<see cref="LspManager"/> 側で解決）。
    /// </summary>
    public required IReadOnlyDictionary<string, string> Servers { get; init; }

    /// <summary>既定値で継続した理由（ファイル破損等）。空なら設定どおり、または新規作成。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>このロードで <c>lsp.yml</c> を新規作成したら true。</summary>
    public required bool Created { get; init; }

    /// <summary>
    /// 不在なら <see cref="DefaultYaml"/> を書き出してからロードする。<c>daemon.yml</c>／<c>plugins.yml</c> は
    /// 不在でも書き出さない（前者は既定値で足りる／後者は必須設定でユーザーに用意させる）が、こちらは
    /// 「言語ごとのダウンロード URL」という編集前提の値のため、雛形が無いと使い始められない。
    /// </summary>
    public static LspConfig Load()
    {
        var warnings = new List<string>();
        var path = InstallPaths.LspConfigPath;
        var created = false;

        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(InstallPaths.ConfigDir);
                File.WriteAllText(path, DefaultYaml);
                created = true;
            }
            catch (Exception ex)
            {
                warnings.Add($"lsp.yml の新規作成に失敗（既定値で継続）: {ex.Message}");
                return new LspConfig { Languages = EmptyLanguages, Servers = EmptyServers, Warnings = warnings, Created = false };
            }
        }

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var model = deserializer.Deserialize<LspYaml>(File.ReadAllText(path));

            var languages = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            if (model?.Languages is { } rawLanguages)
            {
                foreach (var (language, urlsByRid) in rawLanguages)
                {
                    if (urlsByRid is { Count: > 0 })
                    {
                        languages[language] = urlsByRid;
                    }
                }
            }

            var servers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (model?.Servers is { } rawServers)
            {
                foreach (var (language, server) in rawServers)
                {
                    if (!string.IsNullOrWhiteSpace(server))
                    {
                        servers[language] = server;
                    }
                }
            }

            return new LspConfig { Languages = languages, Servers = servers, Warnings = warnings, Created = created };
        }
        catch (Exception ex)
        {
            warnings.Add($"lsp.yml の読み込みに失敗（全て既定値で継続）: {ex.Message}");
            return new LspConfig { Languages = EmptyLanguages, Servers = EmptyServers, Warnings = warnings, Created = created };
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyLanguages =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    private static readonly IReadOnlyDictionary<string, string> EmptyServers =
        new Dictionary<string, string>();

    /// <summary>
    /// 新規作成時の雛形。実在確認済みの URL を <see cref="LspCatalog"/> が対応する言語のうち、
    /// URL 設定が要るものだけ書く（<c>cpp</c> は GitHub 最新リリースを都度解決するため不要、
    /// <c>python</c>／<c>typescript</c>／<c>go</c> は npm／go install のため不要）。
    /// 対応言語を増やすときはここにも追記する。
    /// </summary>
    private const string DefaultYaml = """
        # 言語ごとに使う LSP サーバを選ぶ（複数候補がある言語のみ）。省略時は既定を使う。
        # 選べる値は LspCatalog.Languages[<言語>].Servers のキーに固定（任意のコマンドは指定できない）。
        #   python: pyright（既定・型チェック重視・要 Node.js） | pylsp | jedi（どちらも要 Python のみ・精度は控えめ）
        servers:
          python: pyright

        # LSP サーバのダウンロード元（言語ごと・RID ごとの URL）。
        # 起動コマンドや展開後の実行ファイル位置は ai-harness-main 側で固定（LspCatalog）のため、ここには書かない。
        # 対応言語・サーバは LspCatalog.Languages を参照。無いエントリはここに書いても無視される。
        # cpp（clangd）は asset 名にバージョンが入るため GitHub の最新リリースを都度解決する（ここには書かない）。
        # pyright／typescript（typescript-language-server）／go（gopls）／pylsp／jedi は npm install／go install／
        # pip install のため URL を持たない（node・npm・go・python が実行環境の PATH に入っている前提）。
        languages:
          rust:
            win-x64: https://github.com/rust-lang/rust-analyzer/releases/latest/download/rust-analyzer-x86_64-pc-windows-msvc.zip
            linux-x64: https://github.com/rust-lang/rust-analyzer/releases/latest/download/rust-analyzer-x86_64-unknown-linux-gnu.gz
          java:
            # jdtls は中身が jar のみで RID 非依存の単一 tar.gz（Eclipse の "latest" は常に最新スナップショットへ解決）。
            win-x64: https://download.eclipse.org/jdtls/snapshots/jdt-language-server-latest.tar.gz
            linux-x64: https://download.eclipse.org/jdtls/snapshots/jdt-language-server-latest.tar.gz
        """;

    /// <summary>lsp.yml のデシリアライズ用 DTO。</summary>
    private sealed class LspYaml
    {
        public Dictionary<string, Dictionary<string, string>>? Languages { get; set; }

        /// <summary>言語 → 選択したサーバ名。</summary>
        public Dictionary<string, string>? Servers { get; set; }
    }
}
