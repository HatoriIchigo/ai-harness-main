using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ai_harness_main;

/// <summary>
/// 本体直下 <c>config/plugins.yml</c> のインストール定義。<c>--update</c> がこの定義に従って
/// 各プラグインリポジトリを <c>repos/</c> へ clone／pull・build し、成果 DLL を <c>lib/</c> へ配置する。
/// プロジェクト個別の <see cref="ProjectConfig"/>（有効/無効の切り替え）とは別系統。
///
///   plugins:
///     - path: https://github.com/HatoriIchiogo/ai-harness-file-rules
///       branch: main
/// </summary>
internal sealed class PluginsConfig
{
    /// <summary>baselib リポジトリの既定 URL（<c>baselib</c> 未指定時に使う）。</summary>
    public const string DefaultBaselibPath = "https://github.com/HatoriIchigo/ai-harness-baselib";

    /// <summary>baselib の既定ブランチ。</summary>
    public const string DefaultBaselibBranch = "main";

    /// <summary>
    /// 拡張プラグインが <c>ProjectReference</c> で参照する共有ライブラリ（baselib）の取得元。
    /// 各プラグインの csproj は <c>..\..\ai-harness-baselib\...</c> と兄弟ディレクトリを相対参照するため、
    /// プラグインのビルド前に <c>repos/ai-harness-baselib</c> へ用意する必要がある。<c>baselib</c> 未指定は既定値。
    /// </summary>
    public required PluginEntry Baselib { get; init; }

    /// <summary>インストール対象プラグインの一覧。</summary>
    public required IReadOnlyList<PluginEntry> Plugins { get; init; }

    /// <summary>1 プラグインの定義（リポジトリ URL ＋ ブランチ）。</summary>
    public sealed class PluginEntry
    {
        /// <summary>clone 元のリポジトリ URL。</summary>
        public required string Path { get; init; }

        /// <summary>取得するブランチ（未指定は <c>main</c>）。</summary>
        public required string Branch { get; init; }
    }

    /// <summary>
    /// <paramref name="configPath"/> の <c>plugins.yml</c> を読み込む。
    /// ファイルが無い場合は <c>null</c>（呼び出し側でエラー扱い）。path が空のエントリは除外する。
    /// </summary>
    public static PluginsConfig? Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        var yaml = File.ReadAllText(configPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var model = deserializer.Deserialize<PluginsYaml>(yaml);

        var entries = new List<PluginEntry>();
        foreach (var p in model?.Plugins ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Path))
            {
                continue;
            }
            entries.Add(new PluginEntry
            {
                Path = p.Path.Trim(),
                Branch = string.IsNullOrWhiteSpace(p.Branch) ? "main" : p.Branch.Trim(),
            });
        }

        var baselib = new PluginEntry
        {
            Path = string.IsNullOrWhiteSpace(model?.Baselib?.Path)
                ? DefaultBaselibPath
                : model.Baselib.Path.Trim(),
            Branch = string.IsNullOrWhiteSpace(model?.Baselib?.Branch)
                ? DefaultBaselibBranch
                : model.Baselib.Branch.Trim(),
        };

        return new PluginsConfig { Baselib = baselib, Plugins = entries };
    }

    /// <summary>plugins.yml のデシリアライズ用 DTO。</summary>
    private sealed class PluginsYaml
    {
        public PluginEntryYaml? Baselib { get; set; }
        public List<PluginEntryYaml>? Plugins { get; set; }
    }

    private sealed class PluginEntryYaml
    {
        public string? Path { get; set; }
        public string? Branch { get; set; }
    }
}
