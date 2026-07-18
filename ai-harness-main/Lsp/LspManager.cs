using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// LSP サーバの遅延インストール・起動・停止を担う、daemon プロセス寿命ぶんの静的マネージャ。
/// プラグイン（<see cref="PluginBase"/>）ではなく <c>ai-harness-main</c> 本体に直接組み込む
/// （プラグインはリクエスト毎にインスタンスを使い捨てる隔離モデルのため、常駐プロセスの保持に向かない）。
///
/// 起動タイミングはプロジェクト起動時（<see cref="ProjectContext.Create"/>／<c>Reload</c>）で、
/// <c>common.yml</c> の <c>lsp:</c> に列挙された言語だけを対象にする。呼び出しはバックグラウンドへ
/// 逃がすため <see cref="EnsureStarted"/> 自体はブロックしない（hook の応答を待たせない）。
///
/// インストールは言語単位でグローバルに 1 回だけ・冪等（<see cref="LspCatalog.InstallKind"/> により
/// ダウンロード＋展開／<c>npm install</c>／<c>go install</c> のいずれか）。起動したプロセスは
/// プロジェクトルート＋言語をキーに <see cref="LspProtocolClient"/>（JSON-RPC 会話）と組で保持し、
/// <see cref="StopAll"/>（<see cref="ProjectContext.Dispose"/> 経由。idle 回収・daemon 終了の両方で呼ばれる）
/// で終了させる。
///
/// 診断（<c>textDocument/publishDiagnostics</c>）のキャッシュ公開のみをスコープとする
/// （<see cref="NotifyFileChanged"/>／<see cref="GetDiagnosticsSnapshot"/>）。hover／definition 等の
/// 任意リクエストは扱わない（プラグイン実行モデルとの相性が悪いため。詳細は <see cref="LspProtocolClient"/>）。
/// </summary>
internal static class LspManager
{
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _urlsByLanguage =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    private static IReadOnlyDictionary<string, string> _serverByLanguage =
        new Dictionary<string, string>();

    // 言語 → インストールタスク。Lazy で同一言語の同時インストールを 1 回に絞る。
    // サーバ選択は daemon 起動時に固定（Initialize は 1 回だけ）なので、言語単位のキーで冪等性は保てる。
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> InstallTasks =
        new(StringComparer.Ordinal);

    // (プロジェクトルート, 言語) → 起動プロセス＋プロトコルクライアント。Lazy で同一キーの二重起動を 1 回に絞る。
    private static readonly ConcurrentDictionary<(string ProjectRoot, string Language), Lazy<RunningLsp>> Running =
        new();

    /// <summary>1 つの起動済み LSP（プロセスと、その JSON-RPC 会話を担うクライアント）。</summary>
    private sealed record RunningLsp(Process Process, LspProtocolClient Client);

    // 拡張子 → 言語。「どの LSP を起動するか」には使わない（common.yml の lsp: が唯一の起点）が、
    // 「編集されたファイルをどの起動済み LSP へ同期するか」の判定にはここが要る。
    private static readonly IReadOnlyDictionary<string, string> LanguageByExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".rs"] = "rust",
            [".c"] = "cpp", [".h"] = "cpp", [".cpp"] = "cpp", [".cc"] = "cpp",
            [".cxx"] = "cpp", [".hpp"] = "cpp", [".hxx"] = "cpp",
            [".py"] = "python", [".pyi"] = "python",
            [".ts"] = "typescript", [".tsx"] = "typescript",
            [".js"] = "typescript", [".jsx"] = "typescript", [".mjs"] = "typescript", [".cjs"] = "typescript",
            [".go"] = "go",
            [".java"] = "java",
            [".cs"] = "csharp",
        };

    // (プロジェクトルート, 言語) → 状態スナップショット。HookData.Lsp としてプラグインへ渡す（baselib の型）。
    private static readonly ConcurrentDictionary<(string ProjectRoot, string Language), LspLanguageState> Status =
        new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// <paramref name="projectRoot"/> の LSP 状態スナップショットを返す。<see cref="ProjectContext.RunAsync"/> が
    /// hook 発火直前に <see cref="HookData.Lsp"/> へ注入する。キーは、その言語が一度でもトリガーされていれば
    /// 存在する（未対応言語・未トリガーの言語はキーごと無い）。
    /// </summary>
    public static IReadOnlyDictionary<string, LspLanguageState> GetStatusSnapshot(string projectRoot) =>
        Status
            .Where(kv => string.Equals(kv.Key.ProjectRoot, projectRoot, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key.Language, kv => kv.Value, StringComparer.Ordinal);

    private static void SetStatus(
        (string ProjectRoot, string Language) key, string server, LspServerStatus status, string? error) =>
        Status[key] = new LspLanguageState
        {
            Language = key.Language,
            Server = server,
            Status = status,
            Error = error,
        };

    /// <summary>daemon 起動時に 1 回呼ぶ。<c>lsp.yml</c> の変更反映には <c>--restart</c> が要る（他のグローバル設定と同じ）。</summary>
    public static void Initialize(LspConfig config)
    {
        _urlsByLanguage = config.Languages;
        _serverByLanguage = config.Servers;
    }

    /// <summary>
    /// 言語に対して使う <see cref="LspDefinition"/> とサーバ名を解決する。<c>lsp.yml</c> の <c>servers:</c> で
    /// 選択済みならそれ、未選択・不正値（候補に無い名前）なら <see cref="LspLanguageCatalog.DefaultServer"/>。
    /// 言語自体が <see cref="LspCatalog.Languages"/> に無ければ <c>null</c>。
    /// </summary>
    private static (LspDefinition Definition, string Server)? ResolveServer(string language, Action<LogEntry> log)
    {
        if (!LspCatalog.Languages.TryGetValue(language, out var catalog))
        {
            return null;
        }

        var server = catalog.DefaultServer;
        if (_serverByLanguage.TryGetValue(language, out var configured))
        {
            if (catalog.Servers.ContainsKey(configured))
            {
                server = configured;
            }
            else
            {
                log(LogEntry.Warning(
                    $"{language}: lsp.yml の servers で指定された '{configured}' は候補に無い。既定の '{server}' を使う。")
                    with { Source = "lsp" });
            }
        }

        return (catalog.Servers[server], server);
    }

    /// <summary>
    /// <paramref name="languages"/> の各言語について、未起動ならインストール・起動をバックグラウンドで進める。
    /// 呼び出し元はブロックしない（結果を待たない fire-and-forget）。
    /// </summary>
    public static void EnsureStarted(string projectRoot, IReadOnlyList<string>? languages, Action<LogEntry> log)
    {
        if (languages is not { Count: > 0 })
        {
            return;
        }

        foreach (var language in languages.Distinct(StringComparer.Ordinal))
        {
            var key = (projectRoot, language);
            if (Running.TryGetValue(key, out var existing))
            {
                if (!existing.IsValueCreated || !existing.Value.Process.HasExited)
                {
                    continue; // 起動処理が進行中、または生存中。
                }
                // 起動直後のクラッシュ等で死んでいる。取り除いて次のトリガーで再試行できるようにする。
                if (Running.TryRemove(new KeyValuePair<(string, string), Lazy<RunningLsp>>(key, existing)))
                {
                    var exitCode = existing.Value.Process.ExitCode;
                    log(LogEntry.Warning($"{language} のプロセスが終了していた（exit={exitCode}）。再試行する。")
                        with { Source = "lsp" });
                    SetStatus(key, Status.TryGetValue(key, out var prev) ? prev.Server : language,
                        LspServerStatus.Failed, $"プロセスが終了（exit={exitCode}）");
                    existing.Value.Client.Dispose();
                    existing.Value.Process.Dispose();
                }
            }
            _ = Task.Run(() => EnsureOneAsync(key, log));
        }
    }

    /// <summary>そのプロジェクトの起動済み LSP を全て終了する。idle 回収・daemon 終了の両方から呼ばれる。</summary>
    public static void StopAll(string projectRoot)
    {
        foreach (var key in Running.Keys.Where(k => string.Equals(k.ProjectRoot, projectRoot, StringComparison.Ordinal)).ToList())
        {
            if (!Running.TryRemove(key, out var lazy) || !lazy.IsValueCreated)
            {
                continue;
            }
            try
            {
                if (!lazy.Value.Process.HasExited)
                {
                    lazy.Value.Process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 既に終了している等は無視。
            }
            finally
            {
                lazy.Value.Client.Dispose();
                lazy.Value.Process.Dispose();
            }
        }
        foreach (var key in Status.Keys.Where(k => string.Equals(k.ProjectRoot, projectRoot, StringComparison.Ordinal)).ToList())
        {
            Status.TryRemove(key, out _);
        }
    }

    private static async Task EnsureOneAsync((string ProjectRoot, string Language) key, Action<LogEntry> log)
    {
        var (projectRoot, language) = key;

        var resolved = ResolveServer(language, log);
        if (resolved is not { } r)
        {
            log(LogEntry.Warning($"未対応の言語（対象外）: {language}") with { Source = "lsp" });
            return; // カタログに無い言語は状態も残さない（対象外）。
        }
        var (definition, server) = r;

        SetStatus(key, server, LspServerStatus.Installing, null);

        var rid = RuntimeInformation.RuntimeIdentifier;
        if (!IsRidSupported(definition, rid))
        {
            var reason = $"この環境（{rid}）向けの定義が無い";
            log(LogEntry.Warning($"{language}（{server}）{reason}") with { Source = "lsp" });
            SetStatus(key, server, LspServerStatus.Failed, reason);
            return;
        }

        // サーバごとにディレクトリを分ける（同じ言語でサーバを切り替えても以前の展開物と衝突しない）。
        var installDir = Path.Combine(InstallPaths.LspDir, language, server);
        try
        {
            await EnsureInstalledAsync(language, definition, rid, installDir, log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log(LogEntry.Error($"{language}（{server}）のインストールに失敗: {ex.Message}") with { Source = "lsp" });
            SetStatus(key, server, LspServerStatus.Failed, ex.Message);
            return;
        }

        try
        {
            var lazy = Running.GetOrAdd(key,
                _ => new Lazy<RunningLsp>(() => StartProcess(projectRoot, definition, rid, installDir, log)));
            _ = lazy.Value; // 例外はここで observe される（Lazy が1回だけ実行を保証）。
            SetStatus(key, server, LspServerStatus.Running, null);
        }
        catch (Exception ex)
        {
            log(LogEntry.Error($"{language}（{server}）の起動に失敗: {ex.Message}") with { Source = "lsp" });
            SetStatus(key, server, LspServerStatus.Failed, ex.Message);
            Running.TryRemove(key, out _);
        }
    }

    private static bool IsRidSupported(LspDefinition definition, string rid) => definition.Install switch
    {
        // Download は RID ごとに配布物が違う（Binaries に無ければ非対応）。java もここに含まれるが
        // Binaries のキー集合は JavaConfigDirByRid と一致させてあるので同じ判定で足りる。
        InstallKind.Download => definition.Binaries?.ContainsKey(rid) == true,
        // npm／go／pip はクロスプラットフォームなビルド・パッケージのため RID を問わない。
        InstallKind.NpmInstall or InstallKind.GoInstall or InstallKind.PipInstall or InstallKind.DotnetToolInstall => true,
        _ => false,
    };

    private static async Task<string> EnsureInstalledAsync(
        string language, LspDefinition definition, string rid, string installDir, Action<LogEntry> log)
    {
        var lazy = InstallTasks.GetOrAdd(language,
            _ => new Lazy<Task<string>>(() => InstallAsync(language, definition, rid, installDir, log)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // 失敗を Lazy にキャッシュしたままにすると以後ずっと再試行されない。
            // 次回トリガー（別プロジェクトの起動・Reload 等）で再試行できるようエントリを外す。
            InstallTasks.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(language, lazy));
            throw;
        }
    }

    private static Task<string> InstallAsync(
        string language, LspDefinition definition, string rid, string installDir, Action<LogEntry> log) =>
        definition.Install switch
        {
            InstallKind.Download => InstallByDownloadAsync(language, definition, rid, installDir, log),
            InstallKind.NpmInstall => InstallByNpmAsync(language, definition, installDir, log),
            InstallKind.GoInstall => InstallByGoAsync(language, definition, installDir, log),
            InstallKind.PipInstall => InstallByPipAsync(language, definition, installDir, log),
            InstallKind.DotnetToolInstall => InstallByDotnetToolAsync(language, definition, installDir, log),
            _ => throw new NotSupportedException($"未対応の InstallKind: {definition.Install}"),
        };

    /// <summary>
    /// ダウンロード＋展開でのインストール（rust／cpp／java）。既にインストール済みなら何もしない（冪等）。
    /// 作業は一時ディレクトリで行い、完了後に <c>Directory.Move</c>（同一ボリューム内リネーム）で配置することで、
    /// 展開途中の状態を「インストール済み」と誤認しないようにする（tree-sitter native 自動展開と同じ考え方）。
    /// </summary>
    private static async Task<string> InstallByDownloadAsync(
        string language, LspDefinition definition, string rid, string installDir, Action<LogEntry> log)
    {
        var binary = definition.Binaries![rid];
        var isJava = definition.Launch == LaunchKind.Java;

        if (isJava)
        {
            if (TryFindGlob(installDir, definition.JavaLauncherJarGlob!, out var existingJar))
            {
                return existingJar;
            }
        }
        else if (File.Exists(Path.Combine(installDir, binary.ExecutableRelativePath)))
        {
            return Path.Combine(installDir, binary.ExecutableRelativePath);
        }

        var url = binary.Resolver is { } resolver
            ? await ResolveGitHubAssetUrlAsync(resolver).ConfigureAwait(false)
            : (_urlsByLanguage.TryGetValue(language, out var urlsByRid) && urlsByRid.TryGetValue(rid, out var configuredUrl)
                ? configuredUrl
                : throw new InvalidOperationException($"lsp.yml にダウンロード URL が無い（{rid}）"));

        log(LogEntry.Info($"{language} をダウンロード中: {url}") with { Source = "lsp" });
        // Directory.Move は中間ディレクトリを自動作成しない。installDir の親（lsp/<言語>/）を先に用意する。
        Directory.CreateDirectory(Path.GetDirectoryName(installDir)!);

        var tempDir = Path.Combine(InstallPaths.LspDir, $".{language}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(tempDir);
        try
        {
            var archiveExt = binary.Archive switch
            {
                ArchiveKind.Zip => ".zip",
                ArchiveKind.GZip => ".gz",
                ArchiveKind.TarGZip => ".tar.gz",
                _ => throw new NotSupportedException($"未対応のアーカイブ形式: {binary.Archive}"),
            };
            var archivePath = Path.Combine(tempDir, "download" + archiveExt);
            await using (var response = await Http.GetStreamAsync(url).ConfigureAwait(false))
            await using (var file = File.Create(archivePath))
            {
                await response.CopyToAsync(file).ConfigureAwait(false);
            }

            var extractedDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractedDir);

            switch (binary.Archive)
            {
                case ArchiveKind.Zip:
                    ZipFile.ExtractToDirectory(archivePath, extractedDir);
                    FlattenSingleTopLevelDirectory(extractedDir);
                    break;
                case ArchiveKind.GZip:
                {
                    var outPath = Path.Combine(extractedDir, binary.ExecutableRelativePath);
                    await using var gz = new GZipStream(File.OpenRead(archivePath), CompressionMode.Decompress);
                    await using var outFile = File.Create(outPath);
                    await gz.CopyToAsync(outFile).ConfigureAwait(false);
                    break;
                }
                case ArchiveKind.TarGZip:
                {
                    await using var gz = new GZipStream(File.OpenRead(archivePath), CompressionMode.Decompress);
                    await TarFile.ExtractToDirectoryAsync(gz, extractedDir, overwriteFiles: true).ConfigureAwait(false);
                    break;
                }
            }

            if (isJava)
            {
                if (!TryFindGlob(extractedDir, definition.JavaLauncherJarGlob!, out _))
                {
                    throw new InvalidOperationException(
                        $"展開後に launcher jar が見つからない: {definition.JavaLauncherJarGlob}");
                }
                // jdtls は java -jar 経由の起動のため、jar 自体に実行属性は不要。
            }
            else
            {
                var extractedExecutable = Path.Combine(extractedDir, binary.ExecutableRelativePath);
                if (!File.Exists(extractedExecutable))
                {
                    throw new InvalidOperationException($"展開後に実行ファイルが見つからない: {extractedExecutable}");
                }
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(extractedExecutable,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }

            if (!Directory.Exists(installDir))
            {
                Directory.Move(extractedDir, installDir);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        string finalPath;
        if (isJava)
        {
            if (!TryFindGlob(installDir, definition.JavaLauncherJarGlob!, out var jarPath))
            {
                throw new InvalidOperationException($"配置後に launcher jar が見つからない: {definition.JavaLauncherJarGlob}");
            }
            finalPath = jarPath;
        }
        else
        {
            finalPath = Path.Combine(installDir, binary.ExecutableRelativePath);
            if (!File.Exists(finalPath))
            {
                throw new InvalidOperationException($"配置後に実行ファイルが見つからない: {finalPath}");
            }
        }
        log(LogEntry.Info($"{language} をインストールしました: {finalPath}") with { Source = "lsp" });
        return finalPath;
    }

    /// <summary>
    /// <c>npm install --prefix &lt;installDir&gt;</c> でのインストール（python／typescript）。
    /// 単体バイナリ配布が無い言語向け。<c>node</c>／<c>npm</c> が実行環境の PATH に入っている前提。
    /// </summary>
    private static async Task<string> InstallByNpmAsync(
        string language, LspDefinition definition, string installDir, Action<LogEntry> log)
    {
        var entryPath = Path.Combine(installDir, definition.NodeEntryRelativePath!);
        if (File.Exists(entryPath))
        {
            return entryPath;
        }

        Directory.CreateDirectory(installDir);
        log(LogEntry.Info($"{language} を npm install 中: {string.Join(' ', definition.NpmPackages!)}") with { Source = "lsp" });

        var args = new List<string> { "install", "--prefix", installDir, "--no-audit", "--no-fund" };
        args.AddRange(definition.NpmPackages!);
        await RunCommandAsync("npm", args, workingDirectory: installDir).ConfigureAwait(false);

        if (!File.Exists(entryPath))
        {
            throw new InvalidOperationException($"npm install 後にエントリが見つからない: {entryPath}");
        }
        log(LogEntry.Info($"{language} をインストールしました: {entryPath}") with { Source = "lsp" });
        return entryPath;
    }

    /// <summary>
    /// <c>go install &lt;module&gt;@latest</c> でのインストール（go＝gopls）。<c>GOBIN</c> を
    /// <paramref name="installDir"/>/bin へ上書きし、ビルド成果物を管理下に置く。<c>go</c> が PATH 前提。
    /// </summary>
    private static async Task<string> InstallByGoAsync(
        string language, LspDefinition definition, string installDir, Action<LogEntry> log)
    {
        var binDir = Path.Combine(installDir, "bin");
        var exePath = Path.Combine(binDir, OperatingSystem.IsWindows() ? "gopls.exe" : "gopls");
        if (File.Exists(exePath))
        {
            return exePath;
        }

        Directory.CreateDirectory(binDir);
        log(LogEntry.Info($"{language} を go install 中: {definition.GoModule}@latest") with { Source = "lsp" });

        await RunCommandAsync(
            "go", ["install", $"{definition.GoModule}@latest"],
            workingDirectory: installDir,
            extraEnv: new Dictionary<string, string>(StringComparer.Ordinal) { ["GOBIN"] = binDir })
            .ConfigureAwait(false);

        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException($"go install 後に実行ファイルが見つからない: {exePath}");
        }
        log(LogEntry.Info($"{language} をインストールしました: {exePath}") with { Source = "lsp" });
        return exePath;
    }

    /// <summary>
    /// <c>python -m venv &lt;installDir&gt;</c> の上に <c>pip install</c> でのインストール（pylsp／jedi）。
    /// venv 化することでグローバル site-packages を汚さない。<c>python</c>（Windows）／<c>python3</c>（それ以外）が
    /// PATH 前提。
    /// </summary>
    private static async Task<string> InstallByPipAsync(
        string language, LspDefinition definition, string installDir, Action<LogEntry> log)
    {
        var exePath = PipEntryPath(installDir, definition.PipEntryPoint!);
        if (File.Exists(exePath))
        {
            return exePath;
        }

        Directory.CreateDirectory(InstallPaths.LspDir);
        log(LogEntry.Info($"{language} を pip install 中: {string.Join(' ', definition.PipPackages!)}") with { Source = "lsp" });

        await RunCommandAsync(OperatingSystem.IsWindows() ? "python" : "python3", ["-m", "venv", installDir])
            .ConfigureAwait(false);

        var venvPython = Path.Combine(
            installDir, OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
        var pipArgs = new List<string> { "-m", "pip", "install", "--quiet" };
        pipArgs.AddRange(definition.PipPackages!);
        await RunCommandAsync(venvPython, pipArgs).ConfigureAwait(false);

        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException($"pip install 後にエントリが見つからない: {exePath}");
        }
        log(LogEntry.Info($"{language} をインストールしました: {exePath}") with { Source = "lsp" });
        return exePath;
    }

    /// <summary>venv 内の console_script のフルパス（Windows は <c>Scripts\&lt;entry&gt;.exe</c>、それ以外は <c>bin/&lt;entry&gt;</c>）。</summary>
    private static string PipEntryPath(string installDir, string entryPoint) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(installDir, "Scripts", $"{entryPoint}.exe")
            : Path.Combine(installDir, "bin", entryPoint);

    /// <summary>
    /// <c>dotnet tool install --tool-path &lt;installDir&gt;</c> でのインストール（csharp-ls）。<c>--tool-path</c> は
    /// <paramref name="installDir"/> 直下に実行ファイルを生成する（venv や go の bin/ のようなサブディレクトリは無い）。
    /// <c>dotnet</c> が PATH 前提。
    /// </summary>
    private static async Task<string> InstallByDotnetToolAsync(
        string language, LspDefinition definition, string installDir, Action<LogEntry> log)
    {
        var exePath = DotnetToolEntryPath(installDir, definition.DotnetToolPackage!);
        if (File.Exists(exePath))
        {
            return exePath;
        }

        Directory.CreateDirectory(InstallPaths.LspDir);
        log(LogEntry.Info($"{language} を dotnet tool install 中: {definition.DotnetToolPackage}") with { Source = "lsp" });

        await RunCommandAsync(
            "dotnet", ["tool", "install", "--tool-path", installDir, definition.DotnetToolPackage!])
            .ConfigureAwait(false);

        if (!File.Exists(exePath))
        {
            throw new InvalidOperationException($"dotnet tool install 後に実行ファイルが見つからない: {exePath}");
        }
        log(LogEntry.Info($"{language} をインストールしました: {exePath}") with { Source = "lsp" });
        return exePath;
    }

    /// <summary><c>dotnet tool install --tool-path</c> が生成する実行ファイルのフルパス（Windows は <c>.exe</c> 付き）。</summary>
    private static string DotnetToolEntryPath(string installDir, string package) =>
        Path.Combine(installDir, OperatingSystem.IsWindows() ? $"{package}.exe" : package);

    /// <summary>
    /// 外部コマンドを実行して完了を待つ（<c>npm install</c>／<c>go install</c>／<c>pip install</c> 用）。Windows では
    /// <c>npm</c> 等が <c>.cmd</c> 経由のことがあり、<c>UseShellExecute=false</c> の直接起動では
    /// 実行ファイルを解決できないことがある（実測）ため、<c>cmd.exe /c</c> 経由で OS のコマンド探索に委ねる。
    /// </summary>
    private static async Task RunCommandAsync(
        string command, IReadOnlyList<string> args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi = new ProcessStartInfo(command) { UseShellExecute = false };
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }
        if (extraEnv is not null)
        {
            foreach (var (k, v) in extraEnv)
            {
                psi.Environment[k] = v;
            }
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"プロセス起動に失敗: {command}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{command} {string.Join(' ', args)} が終了コード {process.ExitCode} で失敗: {stderr}");
        }
    }

    /// <summary>
    /// GitHub の最新リリースの asset 一覧から <see cref="GitHubAssetResolver.AssetNamePattern"/>（glob）に
    /// 一致する 1 件を選び、そのダウンロード URL を返す。未認証 API（レート制限 60 回/時/IP）で十分な頻度
    /// （言語単位でグローバルに 1 回だけ・冪等）。
    /// </summary>
    private static async Task<string> ResolveGitHubAssetUrlAsync(GitHubAssetResolver resolver)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.github.com/repos/{resolver.Repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd("ai-harness-main");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name is not null
                && FileSystemName.MatchesSimpleExpression(resolver.AssetNamePattern, name, ignoreCase: true))
            {
                return asset.GetProperty("browser_download_url").GetString()
                    ?? throw new InvalidOperationException(
                        $"{resolver.Repo}: 一致した asset に browser_download_url が無い: {name}");
            }
        }
        throw new InvalidOperationException(
            $"{resolver.Repo}: 最新リリースにパターンへ一致する asset が無い: {resolver.AssetNamePattern}");
    }

    /// <summary>
    /// 展開直後の内容がディレクトリ 1 個だけ（バージョン名などで包まれた配布形式）なら、
    /// その中身を <paramref name="dir"/> 直下へ引き上げる。それ以外（ファイルが直接並ぶ配布形式）は何もしない。
    /// これにより <see cref="LspRidBinary.ExecutableRelativePath"/> をバージョン非依存の固定値にできる。
    /// </summary>
    private static void FlattenSingleTopLevelDirectory(string dir)
    {
        var entries = Directory.GetFileSystemEntries(dir);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return;
        }

        var wrapper = entries[0];
        foreach (var entry in Directory.GetFileSystemEntries(wrapper))
        {
            var dest = Path.Combine(dir, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.Move(entry, dest);
            }
            else
            {
                File.Move(entry, dest);
            }
        }
        Directory.Delete(wrapper);
    }

    /// <summary><paramref name="baseDir"/> 配下で <paramref name="relativeGlob"/>（1 階層のディレクトリ＋ファイル名glob）に一致する最初の 1 件を探す。</summary>
    private static bool TryFindGlob(string baseDir, string relativeGlob, out string path)
    {
        path = "";
        var dirPart = Path.GetDirectoryName(relativeGlob.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        var pattern = Path.GetFileName(relativeGlob);
        var searchDir = Path.Combine(baseDir, dirPart);
        if (!Directory.Exists(searchDir))
        {
            return false;
        }
        var match = Directory.EnumerateFiles(searchDir, pattern).FirstOrDefault();
        if (match is null)
        {
            return false;
        }
        path = match;
        return true;
    }

    private static RunningLsp StartProcess(
        string projectRoot, LspDefinition definition, string rid, string installDir, Action<LogEntry> log)
    {
        var language = definition.Language;
        ProcessStartInfo psi;

        switch (definition.Launch)
        {
            case LaunchKind.Direct:
            {
                var exePath = definition.Install switch
                {
                    InstallKind.Download => Path.Combine(installDir, definition.Binaries![rid].ExecutableRelativePath),
                    InstallKind.GoInstall => Path.Combine(
                        installDir, "bin", OperatingSystem.IsWindows() ? "gopls.exe" : "gopls"),
                    InstallKind.PipInstall => PipEntryPath(installDir, definition.PipEntryPoint!),
                    InstallKind.DotnetToolInstall => DotnetToolEntryPath(installDir, definition.DotnetToolPackage!),
                    _ => throw new NotSupportedException(
                        $"LaunchKind.Direct は InstallKind={definition.Install} 未対応"),
                };
                psi = new ProcessStartInfo(exePath) { UseShellExecute = false };
                foreach (var arg in definition.Args)
                {
                    psi.ArgumentList.Add(arg);
                }
                break;
            }
            case LaunchKind.Node:
            {
                psi = new ProcessStartInfo("node") { UseShellExecute = false };
                psi.ArgumentList.Add(Path.Combine(installDir, definition.NodeEntryRelativePath!));
                foreach (var arg in definition.Args)
                {
                    psi.ArgumentList.Add(arg);
                }
                break;
            }
            case LaunchKind.Java:
            {
                if (!TryFindGlob(installDir, definition.JavaLauncherJarGlob!, out var jarPath))
                {
                    throw new InvalidOperationException($"launcher jar が見つからない: {definition.JavaLauncherJarGlob}");
                }
                var configDir = Path.Combine(installDir, definition.JavaConfigDirByRid![rid]);
                // jdtls は -data（ワークスペース）ごとにインデックスを持つため、プロジェクトごとに専用ディレクトリを用意する。
                var workspaceDir = Path.Combine(
                    InstallPaths.LspDir, language, "workspaces", HashProjectRoot(projectRoot));
                Directory.CreateDirectory(workspaceDir);

                psi = new ProcessStartInfo("java") { UseShellExecute = false };
                foreach (var arg in definition.Args)
                {
                    psi.ArgumentList.Add(arg);
                }
                psi.ArgumentList.Add("-jar");
                psi.ArgumentList.Add(jarPath);
                psi.ArgumentList.Add("-configuration");
                psi.ArgumentList.Add(configDir);
                psi.ArgumentList.Add("-data");
                psi.ArgumentList.Add(workspaceDir);
                break;
            }
            default:
                throw new NotSupportedException($"未対応の LaunchKind: {definition.Launch}");
        }

        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WorkingDirectory = projectRoot;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"プロセス起動に失敗: {language}");
        log(LogEntry.Info($"{language} を起動 pid={process.Id} project={projectRoot}") with { Source = "lsp" });
        // stderr は誰も読まないとパイプバッファが埋まって子プロセスの書き込みがブロックし得る
        // （verbose なログを出す LSP で実際に起こり得る）。読み捨て役を必ず張っておく。
        DrainStderr(process, language, log);

        // stdout は JSON-RPC の読み取りに使う（LspProtocolClient が Content-Length フレーミングで読む）。
        var client = new LspProtocolClient(process, language, log, definition.InitializationSettings);
        client.Start(projectRoot);

        return new RunningLsp(process, client);
    }

    /// <summary>
    /// <paramref name="filePath"/> の拡張子に対応する言語が、そのプロジェクトで起動済みなら
    /// 現在のファイル内容を同期する（<c>didOpen</c>／<c>didChange</c>）。バックグラウンド・非同期・
    /// 例外を投げない（呼び出し元の hook 処理をブロックしない）。対応する起動済み LSP が無ければ何もしない。
    /// </summary>
    public static void NotifyFileChanged(string projectRoot, string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext) || !LanguageByExtension.TryGetValue(ext, out var language))
        {
            return;
        }
        if (!Running.TryGetValue((projectRoot, language), out var lazy) || !lazy.IsValueCreated)
        {
            return;
        }
        var running = lazy.Value;
        if (running.Process.HasExited)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            }
            catch
            {
                return; // 削除・権限エラー等は静かに諦める（次の編集で追いつく）。
            }
            await running.Client.NotifyFileAsync(filePath, content).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// <paramref name="projectRoot"/> で起動済みの全 LSP の診断キャッシュを、絶対パスをキーに束ねて返す。
    /// <see cref="ProjectContext.RunAsync"/> が hook 発火直前に <see cref="HookData.LspDiagnostics"/> へ注入する。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> GetDiagnosticsSnapshot(string projectRoot)
    {
        var merged = new Dictionary<string, IReadOnlyList<LspDiagnostic>>(StringComparer.Ordinal);
        foreach (var (key, lazy) in Running)
        {
            if (!string.Equals(key.ProjectRoot, projectRoot, StringComparison.Ordinal) || !lazy.IsValueCreated)
            {
                continue;
            }
            foreach (var (path, diagnostics) in lazy.Value.Client.GetDiagnosticsByPath())
            {
                merged[path] = diagnostics;
            }
        }
        return merged;
    }

    /// <summary>
    /// <see cref="IFireLspRequester"/> の実体（<c>ai-harness-main</c> 側）。<c>Fire</c> は同期のバッチ処理のため、
    /// ここでは <see cref="NotifyFileChanged"/>／<see cref="GetDiagnosticsSnapshot"/> と違い<b>ブロックしてよい</b>。
    /// 対象言語の LSP がまだインストール・起動中なら、<paramref name="timeout"/> の範囲内でそれも待つ
    /// （<c>Fire</c> は同一言語の複数ファイルを扱うのが普通で、起動待ちが発生するのは実質「その言語の最初の1ファイル」
    /// だけになる）。
    /// </summary>
    public static IReadOnlyList<LspDiagnostic> RequestDiagnosticsSync(
        string projectRoot, string filePath, string content, TimeSpan timeout)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext) || !LanguageByExtension.TryGetValue(ext, out var language))
        {
            return [];
        }

        var deadline = DateTime.UtcNow + timeout;
        var key = (projectRoot, language);
        RunningLsp? running = null;
        while (DateTime.UtcNow < deadline)
        {
            if (Running.TryGetValue(key, out var lazy) && lazy.IsValueCreated && !lazy.Value.Process.HasExited)
            {
                running = lazy.Value;
                break;
            }
            Thread.Sleep(200); // RequestDiagnostics 自体が同期 API（Fire は同期 IEnumerable のため）。
        }
        if (running is null)
        {
            return [];
        }

        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return [];
        }
        return running.Client.RequestDiagnosticsAsync(filePath, content, remaining)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// 子プロセスの stderr を読み切るまで読み捨てる（ログには残す）。放置するとパイプバッファ枯渇で
    /// 子プロセスの書き込みがブロックする（.NET Process の既知の落とし穴）ため、redirect した以上は必須。
    /// </summary>
    private static void DrainStderr(Process process, string language, Action<LogEntry> log)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    log(LogEntry.Debug($"{language}[stderr] {line}") with { Source = "lsp" });
                }
            }
            catch
            {
                // プロセス終了に伴う読み取り中断は無視。
            }
        });
    }

    private static string HashProjectRoot(string projectRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectRoot)))[..16];
}
