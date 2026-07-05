using ai_harness_baselib;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ai_harness_main;

/// <summary>フェーズ 1 つ分の定義。<see cref="Desc"/>＝そのフェーズで作るもの、<see cref="Next"/>＝遷移可能な次フェーズ。</summary>
internal sealed record PhaseNode(string? Desc, IReadOnlyList<string> Next);

/// <summary>
/// プロジェクトの <c>.claude/harness/config/phase.yml</c> が定義するフェーズ遷移グラフ。
/// 形式:
/// <code>
/// phase:
///   req_nfr:
///     desc: "非機能要件を定義する"
///     next: [req_infra, req_backend]
/// </code>
/// 先頭に定義されたフェーズを <see cref="Initial"/>（state 未設定時の初期フェーズ）とする。
/// phase.yml が無ければ実行体隣の <c>resources/phase.yml</c> をコピーして作成する（<see cref="EnsureDefault"/>）。
/// </summary>
internal sealed class PhaseGraph
{
    public const string FileName = "phase.yml";

    private readonly Dictionary<string, PhaseNode> _phases;
    private readonly List<string> _order;

    private PhaseGraph(Dictionary<string, PhaseNode> phases, List<string> order)
    {
        _phases = phases;
        _order = order;
    }

    /// <summary>定義済みフェーズ名（phase.yml の定義順）。</summary>
    public IReadOnlyList<string> Order => _order;

    /// <summary>初期フェーズ（先頭定義）。定義が無ければ <c>null</c>。</summary>
    public string? Initial => _order.Count > 0 ? _order[0] : null;

    /// <summary>指定フェーズが定義されているか。</summary>
    public bool Contains(string phase) => _phases.ContainsKey(phase);

    /// <summary>指定フェーズの次フェーズ候補（未定義・終端は空）。</summary>
    public IReadOnlyList<string> Next(string phase) =>
        _phases.TryGetValue(phase, out var n) ? n.Next : Array.Empty<string>();

    /// <summary>指定フェーズの説明（未定義は <c>null</c>）。</summary>
    public string? Desc(string phase) =>
        _phases.TryGetValue(phase, out var n) ? n.Desc : null;

    /// <summary>
    /// プロジェクト config に <c>phase.yml</c> が無ければ、実行体隣の <c>resources/phase.yml</c> を
    /// コピーして新規作成する。既にあれば何もしない。
    /// </summary>
    public static void EnsureDefault(string configDir, Action<LogEntry> log)
    {
        var dest = Path.Combine(configDir, FileName);
        if (File.Exists(dest))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(configDir);
            var src = Path.Combine(InstallPaths.ResourcesDir, FileName);
            if (!File.Exists(src))
            {
                log(LogEntry.Warning($"既定 {FileName} が見つからないため作成をスキップ: {src}"));
                return;
            }
            File.Copy(src, dest);
            log(LogEntry.Info($"既定 {FileName} を作成: {dest}"));
        }
        catch (Exception ex)
        {
            log(LogEntry.Warning($"{FileName} の既定作成に失敗: {ex.Message}"));
        }
    }

    /// <summary>
    /// <c>config/phase.yml</c> をロードする。ファイルが無い・壊れている場合は空グラフを返す
    /// （呼び出し側で「フェーズ未定義」メッセージにする）。ホットに追従できるよう毎回読み直す想定。
    /// </summary>
    public static PhaseGraph Load(string configDir)
    {
        var path = Path.Combine(configDir, FileName);
        var phases = new Dictionary<string, PhaseNode>(StringComparer.Ordinal);
        var order = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                var yaml = File.ReadAllText(path);
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var model = deserializer.Deserialize<PhaseYaml>(yaml);
                if (model?.Phase is { } map)
                {
                    // Dictionary は挿入順を保つため、定義順で Order を構築する（先頭＝初期フェーズ）。
                    foreach (var (name, node) in map)
                    {
                        if (string.IsNullOrWhiteSpace(name) || phases.ContainsKey(name))
                        {
                            continue;
                        }
                        IReadOnlyList<string> next = node?.Next ?? new List<string>();
                        phases[name] = new PhaseNode(node?.Desc, next);
                        order.Add(name);
                    }
                }
            }
        }
        catch
        {
            // 壊れていれば空グラフ。
        }
        return new PhaseGraph(phases, order);
    }

    /// <summary>phase.yml のデシリアライズ用 DTO。</summary>
    private sealed class PhaseYaml
    {
        public Dictionary<string, PhaseNodeYaml>? Phase { get; set; }
    }

    private sealed class PhaseNodeYaml
    {
        public string? Desc { get; set; }
        public List<string>? Next { get; set; }
    }
}
