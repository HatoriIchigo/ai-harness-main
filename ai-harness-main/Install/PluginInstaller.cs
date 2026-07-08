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

    public static int Run()
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

        if (config.Plugins.Count == 0)
        {
            Console.WriteLine("plugins.yml にインストール対象なし。");
            return ExitOk;
        }

        Directory.CreateDirectory(InstallPaths.ReposDir);
        Directory.CreateDirectory(InstallPaths.LibDir);

        var failed = new List<string>();
        var installed = new List<string>();

        foreach (var entry in config.Plugins)
        {
            Console.WriteLine();
            Console.WriteLine($"==== {entry.Path} ({entry.Branch}) ====");
            try
            {
                var name = InstallOne(entry);
                installed.Add(name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"失敗: {entry.Path} — {ex.Message}");
                failed.Add(entry.Path);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"インストール成功 {installed.Count} 件 / 失敗 {failed.Count} 件。");

        if (failed.Count > 0)
        {
            return ExitError;
        }

        // lib（プラグイン DLL）が変わったため、稼働中の daemon を再起動して反映する。
        if (Daemon.IsRunning())
        {
            Console.WriteLine("daemon を再起動して新しいプラグインを反映。");
            Daemon.Restart();
        }
        return ExitOk;
    }

    /// <summary>1 プラグインを clone／pull・build・配置する。成功でリポジトリ名を返す。失敗は例外。</summary>
    private static string InstallOne(PluginsConfig.PluginEntry entry)
    {
        var name = RepoName(entry.Path);
        var repoDir = Path.Combine(InstallPaths.ReposDir, name);

        // clone または更新（shallow）。
        if (Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            Console.WriteLine($"更新: {repoDir}");
            RunOrThrow("git", ["-C", repoDir, "fetch", "--depth", "1", "origin", entry.Branch]);
            RunOrThrow("git", ["-C", repoDir, "reset", "--hard", "FETCH_HEAD"]);
        }
        else
        {
            Console.WriteLine($"clone: {entry.Path} -> {repoDir}");
            RunOrThrow("git",
                ["clone", "--depth", "1", "--branch", entry.Branch, entry.Path, repoDir]);
        }

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
    private static string FindCsproj(string repoDir, string name)
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
    private static string RepoName(string url)
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
    private static void RunOrThrow(string file, IReadOnlyList<string> args)
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
