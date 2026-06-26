using ai_harness_baselib;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ai_harness_main;

/// <summary>
/// ハーネスの実行設定。ディレクトリは実行体からの相対で固定（環境変数では変更しない）。
/// 可変設定（ログレベル・並列数）は <c>config/main.yml</c> からロードする。
/// 構成は ai-harness-main/README.md に準拠:
///   &lt;実行体ディレクトリ&gt;/lib    … 拡張プラグイン
///   &lt;実行体ディレクトリ&gt;/config … 設定ファイル（main.yml）
/// </summary>
internal sealed class HarnessConfig
{
    /// <summary>プラグイン DLL を走査する固定フォルダ（実行体隣の <c>lib/</c>）。</summary>
    public required string PluginDir { get; init; }

    /// <summary>設定ファイル置き場の固定フォルダ（実行体隣の <c>config/</c>）。</summary>
    public required string ConfigDir { get; init; }

    /// <summary>daemon の作業領域（実行体隣の <c>run/</c>。ロックファイル等）。</summary>
    public required string RunDir { get; init; }

    /// <summary>プラグイン発火の同時実行数上限。</summary>
    public required int MaxParallel { get; init; }

    /// <summary>この閾値以上のレベルのログのみ出力する。</summary>
    public required LogLevel MinLogLevel { get; init; }

    public const string ConfigFileName = "main.yml";

    /// <summary>
    /// 実行体ディレクトリ基準で固定構成を生成し、<c>config/main.yml</c> から可変設定を上書きする。
    /// 設定ファイルが無い・壊れている場合は既定値（logLevel=Info, maxParallel=論理プロセッサ数）。
    /// </summary>
    public static HarnessConfig Load(out string? configWarning)
    {
        configWarning = null;

        var baseDir = AppContext.BaseDirectory;
        var configDir = Path.Combine(baseDir, "config");

        var minLogLevel = LogLevel.Info;
        var maxParallel = Environment.ProcessorCount;

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
                var model = deserializer.Deserialize<MainYaml>(yaml);

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
                }
            }
            catch (Exception ex)
            {
                configWarning = $"{ConfigFileName} の読み込みに失敗（既定値を使用）: {ex.Message}";
            }
        }

        return new HarnessConfig
        {
            PluginDir = Path.Combine(baseDir, "lib"),
            ConfigDir = configDir,
            RunDir = Path.Combine(baseDir, "run"),
            MaxParallel = maxParallel,
            MinLogLevel = minLogLevel,
        };
    }

    /// <summary>config/main.yml のデシリアライズ用 DTO。</summary>
    private sealed class MainYaml
    {
        public string? LogLevel { get; set; }
        public int? MaxParallel { get; set; }
    }
}
