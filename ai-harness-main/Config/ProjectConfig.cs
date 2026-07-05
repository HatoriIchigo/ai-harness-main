using ai_harness_baselib;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ai_harness_main;

/// <summary>
/// プロジェクト個別の実行設定。プロジェクトルート（<c>.claude</c> を含むディレクトリ）配下の
/// <c>.claude/harness/config/common.yml</c> からロードする。単一 daemon が複数プロジェクトをさばくため、
/// 設定・ログは実行体ではなくプロジェクトごとに分離する。
///
///   &lt;ルート&gt;/.claude/harness/config/  … common.yml ＋ 各プラグイン YAML
///   &lt;ルート&gt;/.claude/harness/logs/    … 統合ログ（&lt;yyyy-MM-dd&gt;.jsonl）
/// </summary>
internal sealed class ProjectConfig
{
    /// <summary>プロジェクトルート（<c>.claude</c> を含む階層の絶対パス）。</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>設定ファイル置き場（<c>&lt;ルート&gt;/.claude/harness/config</c>）。</summary>
    public required string ConfigDir { get; init; }

    /// <summary>ログ出力先（<c>&lt;ルート&gt;/.claude/harness/logs</c>）。</summary>
    public required string LogDir { get; init; }

    /// <summary>プラグイン発火の同時実行数上限。</summary>
    public required int MaxParallel { get; init; }

    /// <summary>この閾値以上のレベルのログのみ出力する。</summary>
    public required LogLevel MinLogLevel { get; init; }

    /// <summary>
    /// プラグイン（PluginName）ごとの有効/無効。<c>common.yml</c> の <c>tools</c> から構築する。
    /// <c>true</c> で有効化、<c>false</c> および未記載は無効。
    /// </summary>
    public required IReadOnlyDictionary<string, bool> ToolToggles { get; init; }

    /// <summary>
    /// <c>common.yml</c> が<b>存在するのに解析に失敗</b>したときのエラー内容（それ以外は <c>null</c>）。
    /// 設定ファイルが無いプロジェクト（ハーネス未使用）は <c>null</c> のまま＝対象外。
    /// フェイルクローズ方針では「在るのに壊れている＝何を強制すべきか判断できない」ため、host はこの
    /// プロジェクトの hook をブロックする（<see cref="ProjectContext.RunAsync"/> で判定）。
    /// </summary>
    public string? LoadError { get; init; }

    public const string ConfigFileName = "common.yml";

    /// <summary>このプロジェクトの設定ファイル（<c>common.yml</c>）の絶対パス。</summary>
    public string ConfigFilePath => Path.Combine(ConfigDir, ConfigFileName);

    /// <summary>
    /// プロジェクトルート基準で固定構成を生成し、<c>common.yml</c> から可変設定を上書きする。
    /// 設定ファイルが無い・壊れている場合は既定値（logLevel=Info, maxParallel=論理プロセッサ数, プラグイン全 off）。
    /// </summary>
    public static ProjectConfig Load(string projectRoot, out string? configWarning)
    {
        configWarning = null;

        var harnessDir = Path.Combine(projectRoot, InstallPaths.HarnessSubdir);
        var configDir = Path.Combine(harnessDir, "config");
        var logDir = Path.Combine(harnessDir, "logs");

        var minLogLevel = LogLevel.Info;
        var maxParallel = Environment.ProcessorCount;
        // 既定は空＝全プラグイン無効（「書いていないときは off」）。
        var toolToggles = new Dictionary<string, bool>(StringComparer.Ordinal);

        var configPath = Path.Combine(configDir, ConfigFileName);
        if (File.Exists(configPath))
        {
            try
            {
                var yaml = File.ReadAllText(configPath);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var model = deserializer.Deserialize<CommonYaml>(yaml);

                if (model is not null)
                {
                    if (!string.IsNullOrWhiteSpace(model.LogLevel)
                        && Enum.TryParse<LogLevel>(model.LogLevel, ignoreCase: true, out var parsed))
                    {
                        minLogLevel = parsed;
                    }

                    if (model.MaxParallel is { } mp && mp > 0)
                    {
                        maxParallel = mp;
                    }

                    // tools: 各要素は { PluginName: true/false } の単一エントリマップ。重複キーは後勝ち。
                    if (model.Tools is { } tools)
                    {
                        foreach (var entry in tools)
                        {
                            foreach (var kv in entry)
                            {
                                toolToggles[kv.Key] = kv.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                configWarning = $"{ConfigFileName} の読み込みに失敗（既定値を使用）: {ex.Message}";
            }
        }

        return new ProjectConfig
        {
            ProjectRoot = projectRoot,
            ConfigDir = configDir,
            LogDir = logDir,
            MaxParallel = maxParallel,
            MinLogLevel = minLogLevel,
            ToolToggles = toolToggles,
            LoadError = configWarning, // 非 null＝common.yml が在るのに壊れている（フェイルクローズ対象）
        };
    }

    /// <summary>common.yml のデシリアライズ用 DTO。</summary>
    private sealed class CommonYaml
    {
        public string? LogLevel { get; set; }
        public int? MaxParallel { get; set; }

        /// <summary>各要素は <c>{ PluginName: true/false }</c> の単一エントリマップ。</summary>
        public List<Dictionary<string, bool>>? Tools { get; set; }
    }
}
