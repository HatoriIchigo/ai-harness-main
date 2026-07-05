using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// プロジェクト単位の永続 state（<c>&lt;projectRoot&gt;/.claude/harness/state.json</c>）を
/// メモリに保持し、外部編集のホットリロードと main 由来の差分書き込みを両立させる。
///
/// <list type="bullet">
///   <item>読み: <see cref="Current"/> が現在の state 全体（常に非 null）。main が発火前に
///     <see cref="HookData.State"/> へ注入する。</item>
///   <item>書き: <see cref="ApplyAndSave"/> が各プラグインの返した state スライスを
///     PluginName の名前空間ごとに上書きし、state 全体に差分がある場合のみ atomic に書き戻す。</item>
///   <item>ホットリロード: <c>state.json</c> の外部変更を監視してメモリへ反映する。ただし
///     自分の書き込みは書いたバイト列のハッシュで識別して無視し、書き込み→再ロードのループを防ぐ。</item>
/// </list>
///
/// state はプロジェクト単位の単一ファイル。<see cref="ProjectContext"/> が 1 つ保持し、
/// 設定のホットリロード（有効プラグイン再構築）とは独立に生存する。ディスクを真実源とするため、
/// <see cref="ProjectContext"/> がアイドル回収・daemon 再起動されても次回ロードで復元できる。
/// </summary>
internal sealed class StateStore : IDisposable
{
    private const int DebounceMs = 500;
    private const string FileName = "state.json";

    private readonly string _filePath;
    private readonly string _dir;
    private readonly Action<LogEntry> _log;
    private readonly object _lock = new();

    // Current / Reload / ApplyAndSave から読み書きされるため volatile。差し替えは常に新インスタンス。
    private volatile JsonObject _state;

    // 直近に自分が書き込んだファイルバイト列のハッシュ。ホットリロード時の自己書き込み判定に使う。
    private string? _lastWrittenHash;

    private FileSystemWatcher? _watcher;
    private int _reloadGen;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private StateStore(string filePath, string dir, Action<LogEntry> log, JsonObject state)
    {
        _filePath = filePath;
        _dir = dir;
        _log = log;
        _state = state;
    }

    /// <summary>現在の state 全体（常に非 null の JsonObject）。共有参照のため呼び出し側は書き換えないこと。</summary>
    public JsonNode Current => _state;

    /// <summary>state 予約キー: 現在フェーズ（main が管理。プラグインは <c>data.State["phase"]</c> で読める）。</summary>
    public const string PhaseKey = "phase";

    /// <summary>現在フェーズ（トップレベル <see cref="PhaseKey"/>）。未設定・型不一致は <c>null</c>。</summary>
    public string? GetPhase()
    {
        try
        {
            return _state[PhaseKey] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>現在フェーズを設定する（差分があれば state.json に書き戻す）。</summary>
    public void SetPhase(string phase) =>
        ApplyAndSave(new Dictionary<string, JsonNode> { [PhaseKey] = JsonValue.Create(phase)! });

    /// <summary>state.json をロードしてストアを構築し、ホットリロード監視を開始する。</summary>
    public static StateStore Create(string projectRoot, Action<LogEntry> log)
    {
        var dir = Path.Combine(projectRoot, InstallPaths.HarnessSubdir);
        var filePath = Path.Combine(dir, FileName);
        var state = LoadFile(filePath, log);
        var store = new StateStore(filePath, dir, log, state);
        store.StartWatching();
        return store;
    }

    private static JsonObject LoadFile(string filePath, Action<LogEntry> log)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new JsonObject();
            }
            var text = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            log(LogEntry.Warning($"state.json の読み込みに失敗（空 state を使用）: {ex.Message}"));
            return new JsonObject();
        }
    }

    // ---- 書き込み ----

    /// <summary>
    /// 各プラグインが返した state スライス（PluginName → 新しい値）を現在 state へ名前空間ごとに
    /// 上書きし、state 全体に差分がある場合のみ書き戻す。差分が無ければ何もしない。
    /// read-modify-write は <see cref="_lock"/> で直列化する（サブエージェントの割り込み対策）。
    /// </summary>
    public void ApplyAndSave(IReadOnlyDictionary<string, JsonNode> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }
        lock (_lock)
        {
            var current = _state;
            var next = (JsonObject)current.DeepClone();
            foreach (var (name, slice) in updates)
            {
                // slice が共有 state を参照している可能性に備えクローンして親子関係を切る。
                next[name] = slice.DeepClone();
            }

            if (JsonNode.DeepEquals(current, next))
            {
                return; // 差分なし → 書かない
            }

            _state = next;
            Save(next);
        }
    }

    private void Save(JsonObject state)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var bytes = Encoding.UTF8.GetBytes(state.ToJsonString(WriteOptions));
            _lastWrittenHash = Hash(bytes); // 自己書き込み抑止（ウォッチャが拾ったら無視するため）

            var tmp = _filePath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _filePath, overwrite: true); // atomic replace
            _log(LogEntry.Debug("state.json を更新"));
        }
        catch (Exception ex)
        {
            _log(LogEntry.Error($"state.json の書き込みに失敗: {ex.Message}"));
        }
    }

    // ---- ホットリロード（外部編集） ----

    private void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var watcher = new FileSystemWatcher(_dir)
            {
                Filter = FileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            _log(LogEntry.Warning($"state.json の監視開始に失敗（ホットリロード無効）: {ex.Message}"));
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // デバウンス: 連続イベント（temp→rename 等）を束ね、最後の 1 回だけ反映する。
        var token = Interlocked.Increment(ref _reloadGen);
        _ = Task.Run(async () =>
        {
            await Task.Delay(DebounceMs).ConfigureAwait(false);
            if (Volatile.Read(ref _reloadGen) != token)
            {
                return; // 後続イベントに追い越された
            }
            Reload();
        });
    }

    private void Reload()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _state = new JsonObject(); // 削除されたら空 state 扱い
                    return;
                }
                var bytes = File.ReadAllBytes(_filePath);
                if (Hash(bytes) == _lastWrittenHash)
                {
                    return; // 自分が書いたもの → 再ロードしない（ループ防止）
                }
                var text = Encoding.UTF8.GetString(bytes);
                if (string.IsNullOrWhiteSpace(text))
                {
                    _state = new JsonObject();
                    return;
                }
                if (JsonNode.Parse(text) is JsonObject obj)
                {
                    _state = obj;
                    _log(LogEntry.Info("state.json を再読み込み（外部変更）"));
                }
            }
            catch (Exception ex)
            {
                _log(LogEntry.Warning($"state.json の再読み込みに失敗（従前を継続）: {ex.Message}"));
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        try
        {
            if (_watcher is { } w)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
        }
        catch
        {
            // 破棄失敗は無視。
        }
        _watcher = null;
    }
}
