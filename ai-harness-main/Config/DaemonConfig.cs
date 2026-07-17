using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ai_harness_main;

/// <summary>
/// daemon（常駐プロセス）の寿命に関する設定。実行体隣の <c>config/daemon.yml</c> からロードする。
///
/// 単一の共有 daemon が全プロジェクトをさばくため、この設定はプロジェクト個別（<see cref="ProjectConfig"/>
/// ＝<c>common.yml</c>）ではなくグローバル（<see cref="InstallPaths.ConfigDir"/>、<c>plugins.yml</c> と同列）。
/// 読むのは daemon 起動時の 1 回のみで、変更の反映には <c>--restart</c> が要る（プロジェクト個別設定の
/// ホットリロードとは別系統）。
///
/// 何も強制しない実行時パラメータのため、ファイル不在・破損・不正値は<b>既定値で継続</b>する。
/// <c>common.yml</c> が「在るのに壊れている」ときフェイルクローズするのは、何を強制すべきか
/// 決められないため＝性質が異なる。
/// </summary>
internal sealed class DaemonConfig
{
    /// <summary>プロジェクトが無アクセスでこれを超えたらキャッシュを回収する。</summary>
    public required TimeSpan EvictAfter { get; init; }

    /// <summary>接続が無い状態がこれを超えたら、生存プロジェクトの有無を確認する（空なら終了）。</summary>
    public required TimeSpan IdleShutdown { get; init; }

    /// <summary>スイーパの走査間隔（回収判定の粒度）。</summary>
    public required TimeSpan SweepInterval { get; init; }

    /// <summary>既定値で継続した理由（ファイル破損・不正値）。空なら設定どおり。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    public const string ConfigFileName = "daemon.yml";

    /// <summary>設定ファイル（<c>&lt;実行体&gt;/config/daemon.yml</c>）の絶対パス。</summary>
    public static string ConfigFilePath => Path.Combine(InstallPaths.ConfigDir, ConfigFileName);

    private const int DefaultEvictAfterMinutes = 30;
    private const int DefaultIdleShutdownMinutes = 5;
    private const int DefaultSweepIntervalSeconds = 60;

    /// <summary><c>daemon.yml</c> から可変設定をロードする。不在・破損・不正値は既定値。</summary>
    public static DaemonConfig Load()
    {
        var warnings = new List<string>();
        var evictMinutes = DefaultEvictAfterMinutes;
        var idleMinutes = DefaultIdleShutdownMinutes;
        var sweepSeconds = DefaultSweepIntervalSeconds;

        var path = ConfigFilePath;
        if (File.Exists(path))
        {
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var model = deserializer.Deserialize<DaemonYaml>(File.ReadAllText(path));

                if (model is not null)
                {
                    evictMinutes = Positive(
                        model.EvictAfterMinutes, evictMinutes, "evictAfterMinutes", warnings);
                    idleMinutes = Positive(
                        model.IdleShutdownMinutes, idleMinutes, "idleShutdownMinutes", warnings);
                    sweepSeconds = Positive(
                        model.SweepIntervalSeconds, sweepSeconds, "sweepIntervalSeconds", warnings);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"{ConfigFileName} の読み込みに失敗（全て既定値で継続）: {ex.Message}");
            }
        }

        return new DaemonConfig
        {
            EvictAfter = TimeSpan.FromMinutes(evictMinutes),
            IdleShutdown = TimeSpan.FromMinutes(idleMinutes),
            SweepInterval = TimeSpan.FromSeconds(sweepSeconds),
            Warnings = warnings,
        };
    }

    /// <summary>1 以上の整数のみ採用。未指定は既定値、0 以下は警告して既定値。</summary>
    private static int Positive(int? value, int fallback, string key, List<string> warnings)
    {
        if (value is not { } v)
        {
            return fallback;
        }
        if (v > 0)
        {
            return v;
        }
        warnings.Add($"{ConfigFileName}: {key} は 1 以上の整数（値={v}）。既定 {fallback} で継続。");
        return fallback;
    }

    /// <summary>daemon.yml のデシリアライズ用 DTO。</summary>
    private sealed class DaemonYaml
    {
        public int? EvictAfterMinutes { get; set; }
        public int? IdleShutdownMinutes { get; set; }
        public int? SweepIntervalSeconds { get; set; }
    }
}
