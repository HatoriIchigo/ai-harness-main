using System.Diagnostics;

namespace ai_harness_main;

/// <summary>
/// <c>--update</c> の実体。本体直下 <c>config/plugins.yml</c> の定義に従い、拡張プラグインを
/// <c>repos/</c>（実行体隣）へ clone／pull・build し、成果の管理 DLL を <c>lib/</c> へ配置する。
///
/// 本体（<c>ai-harness-main</c> 自身）の更新は対象外。拡張プラグインのみを扱う。
/// 前提コマンド（<c>git</c>／<c>dotnet</c>）が PATH に無ければ何もせず異常終了（非 0）。
///
/// 出力は手動実行の CLI として stdout／stderr へ直接書く（daemon のログ経路は使わない）。
/// </summary>
internal static class PluginInstaller
{
    private const int ExitOk = 0;
    private const int ExitError = 1;

    /// <param name="pluginName">
    /// 指定時はその 1 プラグインのみ更新する（<c>--update &lt;plugin name&gt;</c>）。名前は plugins.yml の各
    /// エントリのリポジトリ名（URL 末尾）と照合する。null（引数なしの <c>--update</c>）は全プラグイン＋本体自己更新。
    /// </param>
    public static int Run(string? pluginName = null)
    {
        // 前提: git / dotnet が無ければ異常終了。
        if (!CommandExists("git"))
        {
            Console.Error.WriteLine("git が見つからない。git をインストールしてから再実行。");
            return ExitError;
        }
        if (!CommandExists("dotnet"))
        {
            Console.Error.WriteLine("dotnet が見つからない。.NET SDK をインストールしてから再実行。");
            return ExitError;
        }

        // 定義の読み込み。
        PluginsConfig? config;
        try
        {
            config = PluginsConfig.Load(InstallPaths.PluginsConfigPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"plugins.yml の解析に失敗: {ex.Message}");
            return ExitError;
        }

        if (config is null)
        {
            Console.Error.WriteLine($"設定ファイルが無い: {InstallPaths.PluginsConfigPath}");
            Console.Error.WriteLine("plugins.yml を作成してから再実行（例は docs/configuration.md 参照）。");
            return ExitError;
        }

        Directory.CreateDirectory(InstallPaths.ReposDir);
        Directory.CreateDirectory(InstallPaths.LibDir);

        return pluginName is null ? RunAll(config) : RunSingle(config, pluginName);
    }

    /// <summary>全プラグイン＋本体自己更新（引数なしの <c>--update</c>）。従来動作。</summary>
    private static int RunAll(PluginsConfig config)
    {
        // ---- プラグイン更新（同期） ----
        if (config.Plugins.Count > 0)
        {
            // 拡張プラグインは baselib を兄弟ディレクトリ相対参照（..\..\ai-harness-baselib\...）でビルド時参照する。
            // プラグインのビルド前に repos/ai-harness-baselib へ用意する。ここが無いと全プラグインのビルドが失敗する。
            // baselib は「本体（ai-harness-main）」ではなくプラグインのビルド依存。稼働中の本体バイナリは差し替えない。
            try
            {
                Console.WriteLine($"==== baselib: {config.Baselib.Path} ({config.Baselib.Branch}) ====");
                var baselibDir = Path.Combine(InstallPaths.ReposDir, BaselibDirName);
                CloneOrUpdate(config.Baselib.Path, config.Baselib.Branch, baselibDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"baselib の用意に失敗（プラグインをビルドできない）: {ex.Message}");
                return ExitError;
            }

            var failed = new List<string>();
            var installed = new List<string>();
            foreach (var entry in config.Plugins)
            {
                Console.WriteLine();
                Console.WriteLine($"==== {entry.Path} ({entry.Branch}) ====");
                try
                {
                    installed.Add(InstallOne(entry));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"失敗: {entry.Path} — {ex.Message}");
                    failed.Add(entry.Path);
                }
            }

            Console.WriteLine();
            Console.WriteLine($"プラグイン: 成功 {installed.Count} 件 / 失敗 {failed.Count} 件。");
            if (failed.Count > 0)
            {
                // プラグインが壊れている状態で本体を差し替えない（まず環境を直す）。
                return ExitError;
            }
        }
        else
        {
            Console.WriteLine("plugins.yml にプラグイン対象なし（本体更新のみ実施）。");
        }

        // ---- 本体自己更新（新版を tmp へ publish → applier へハンドオフ） ----
        bool handedOff = false;
        var selfCode = ExitOk;
        try
        {
            Console.WriteLine();
            Console.WriteLine($"==== self: {config.Self.Path} ({config.Self.Branch}) ====");
            handedOff = SelfUpdater.Run(config.Self, config.Baselib);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"本体更新に失敗（本体は据え置き）: {ex.Message}");
            selfCode = ExitError;
        }

        if (handedOff)
        {
            // applier が daemon 停止→exe 置換→再起動まで行う（新 lib と新バイナリの両方を反映）。
            // Windows は自プロセス終了が置換の条件のため、置換結果はここでは同期で分からない（logs/ で確認）。
            Console.WriteLine("本体更新をバックグラウンドで適用中。結果は logs/ を参照。");
            return ExitOk;
        }

        // ハンドオフ無し（本体更新をスキップ／失敗）: プラグイン変更を反映するため daemon を再起動する。
        if (Daemon.IsRunning())
        {
            Console.WriteLine("daemon を再起動して変更を反映。");
            Daemon.Restart();
        }
        return selfCode;
    }

    /// <summary>
    /// 単一プラグインのみ更新する（<c>--update &lt;plugin name&gt;</c>）。<paramref name="pluginName"/> は
    /// plugins.yml 各エントリのリポジトリ名（URL 末尾）と照合する。本体自己更新は行わず、新 DLL を反映するため
    /// daemon を再起動する。名前が一致しない場合は指定可能な名前を示して異常終了。
    /// </summary>
    private static int RunSingle(PluginsConfig config, string pluginName)
    {
        var entry = config.Plugins.FirstOrDefault(
            p => string.Equals(RepoName(p.Path), pluginName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Console.Error.WriteLine($"plugins.yml に一致するプラグインがない: {pluginName}");
            Console.Error.WriteLine(config.Plugins.Count == 0
                ? "plugins.yml に plugins の対象がない。"
                : $"指定できるプラグイン: {string.Join(", ", config.Plugins.Select(p => RepoName(p.Path)))}");
            return ExitError;
        }

        // 拡張プラグインは baselib を兄弟ディレクトリ相対参照でビルド時参照するため、ビルド前に用意する。
        try
        {
            Console.WriteLine($"==== baselib: {config.Baselib.Path} ({config.Baselib.Branch}) ====");
            var baselibDir = Path.Combine(InstallPaths.ReposDir, BaselibDirName);
            CloneOrUpdate(config.Baselib.Path, config.Baselib.Branch, baselibDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"baselib の用意に失敗（プラグインをビルドできない）: {ex.Message}");
            return ExitError;
        }

        try
        {
            Console.WriteLine();
            Console.WriteLine($"==== {entry.Path} ({entry.Branch}) ====");
            InstallOne(entry);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失敗: {entry.Path} — {ex.Message}");
            return ExitError;
        }

        // 単一プラグイン更新は本体自己更新を伴わない。新 DLL を反映するため daemon を再起動する。
        if (Daemon.IsRunning())
        {
            Console.WriteLine("daemon を再起動して変更を反映。");
            Daemon.Restart();
        }
        Console.WriteLine();
        Console.WriteLine($"プラグイン {RepoName(entry.Path)} を更新しました。");
        return ExitOk;
    }

    /// <summary>
    /// repos/ai-harness-baselib のディレクトリ名（固定）。プラグインの相対参照
    /// <c>..\..\ai-harness-baselib\...</c> が解決するよう、clone 先はこの名前に固定する。
    /// </summary>
    private const string BaselibDirName = "ai-harness-baselib";

    /// <summary>1 プラグインを clone／pull・build・配置する。成功でリポジトリ名を返す。失敗は例外。</summary>
    private static string InstallOne(PluginsConfig.PluginEntry entry)
    {
        var name = RepoName(entry.Path);
        var repoDir = Path.Combine(InstallPaths.ReposDir, name);

        CloneOrUpdate(entry.Path, entry.Branch, repoDir);

        // build（管理 DLL のみを専用出力先へ）。
        var csproj = FindCsproj(repoDir, name);
        var buildOut = Path.Combine(repoDir, ".harness-build");
        Console.WriteLine($"build: {csproj}");
        RunOrThrow("dotnet", ["build", csproj, "-c", "Release", "-o", buildOut]);

        // 成果 DLL を lib/ へ配置（baselib は host が共有ロードするため除外）。
        var copied = CopyPluginDlls(buildOut, InstallPaths.LibDir);
        if (copied == 0)
        {
            throw new InvalidOperationException($"build 出力に配置対象 DLL が無い: {buildOut}");
        }
        Console.WriteLine($"配置: {copied} 個の DLL を {InstallPaths.LibDir} へ");
        return name;
    }

    /// <summary>
    /// <paramref name="repoDir"/> にリポジトリを用意する。既存（<c>.git</c> あり）なら
    /// <paramref name="branch"/> を fetch して <c>reset --hard FETCH_HEAD</c> で最新化、無ければ shallow clone。
    /// </summary>
    internal static void CloneOrUpdate(string url, string branch, string repoDir)
    {
        if (Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            Console.WriteLine($"更新: {repoDir}");
            RunOrThrow("git", ["-C", repoDir, "fetch", "--depth", "1", "origin", branch]);
            RunOrThrow("git", ["-C", repoDir, "reset", "--hard", "FETCH_HEAD"]);
        }
        else
        {
            Console.WriteLine($"clone: {url} -> {repoDir}");
            RunOrThrow("git", ["clone", "--depth", "1", "--branch", branch, url, repoDir]);
        }
    }

    /// <summary>build 出力から baselib を除く *.dll を lib/ へ上書きコピーし、コピー数を返す。</summary>
    private static int CopyPluginDlls(string buildOut, string libDir)
    {
        var count = 0;
        foreach (var dll in Directory.EnumerateFiles(buildOut, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(dll), "ai-harness-baselib",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            File.Copy(dll, Path.Combine(libDir, Path.GetFileName(dll)), overwrite: true);
            count++;
        }
        return count;
    }

    /// <summary>
    /// リポジトリ内の csproj を特定する。bin／obj 配下は除外。
    /// リポジトリ名と一致するものを優先。単一ならそれを使う。複数で一致無しは曖昧として例外。
    /// </summary>
    internal static string FindCsproj(string repoDir, string name)
    {
        // bin／obj は「リポジトリ内の」相対パスで判定する。インストール先自体が bin/ 配下（例:
        // 開発時の bin/Release/net10.0）でも、絶対パスの bin を誤検出しないようにする。
        var all = Directory.EnumerateFiles(repoDir, "*.csproj", SearchOption.AllDirectories)
            .Where(p =>
            {
                var rel = Path.GetRelativePath(repoDir, p);
                var segs = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segs.Any(s =>
                    string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        if (all.Count == 0)
        {
            throw new InvalidOperationException($"csproj が見つからない: {repoDir}");
        }

        var match = all.FirstOrDefault(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }
        if (all.Count == 1)
        {
            return all[0];
        }
        throw new InvalidOperationException(
            $"csproj が複数ありリポジトリ名（{name}）と一致するものが無い: {string.Join(", ", all.Select(Path.GetFileName))}");
    }

    /// <summary>リポジトリ URL から末尾のリポジトリ名を取り出す（末尾スラッシュ・<c>.git</c> を除去）。</summary>
    internal static string RepoName(string url)
    {
        var trimmed = url.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var last = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            last = last[..^4];
        }
        if (string.IsNullOrWhiteSpace(last))
        {
            throw new InvalidOperationException($"リポジトリ名を特定できない URL: {url}");
        }
        return last;
    }

    /// <summary><paramref name="file"/> を実行し、非 0 終了なら例外。出力は親コンソールへそのまま流す。</summary>
    internal static void RunOrThrow(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"プロセス起動に失敗: {file}");
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{file} {string.Join(' ', args)} が終了コード {p.ExitCode} で失敗");
        }
    }

    /// <summary><paramref name="cmd"/> が PATH で実行可能か（<c>--version</c> を静かに実行して判定）。</summary>
    private static bool CommandExists(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            p.WaitForExit();
            return true;
        }
        catch
        {
            // 実行体が見つからない（Win32Exception 等）は未インストールとみなす。
            return false;
        }
    }
}
