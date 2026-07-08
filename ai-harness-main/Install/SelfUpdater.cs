using System.Diagnostics;
using System.Runtime.InteropServices;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 本体（ai-harness-main 自身）の自己更新。<c>--update</c> のプラグイン更新後に呼ばれる。
///
/// 稼働中の実行ファイルは自分自身では上書きできない（特に Windows はロック）。そこで
///   1. <see cref="Run"/>（稼働中の実行体）: 本体＋baselib を tmp へ clone → self-contained single-file で publish →
///      publish 済みの<b>新バイナリ</b>を <c>--apply-update</c> モードで detached 起動し、自身は終了する。
///   2. <see cref="ApplyUpdate"/>（tmp の新バイナリ）: 旧プロセス終了と daemon 停止を待ち、インストール先の
///      実行体を退避（.bak）→ 新バイナリで上書き → 起動検証（失敗はロールバック）→ daemon 再起動 → tmp 掃除。
///
/// applier は「置換対象 exe とは別ファイル（tmp の新バイナリ）」ゆえロックに縛られず置換できる。ヘルパは
/// bat/sh ではなく本体と同一コードベースの 1 モード。
/// </summary>
internal static class SelfUpdater
{
    private const string ApplyMode = "--apply-update";

    /// <summary>
    /// 本体を tmp へ publish し、新バイナリの applier へハンドオフする。ハンドオフ起動できたら <c>true</c>。
    /// <c>dotnet &lt;dll&gt;</c> 経由起動など置換対象を特定できない場合は警告して <c>false</c>（スキップ）。
    /// clone／publish／検証に失敗したら例外。
    /// </summary>
    public static bool Run(PluginsConfig.PluginEntry self, PluginsConfig.PluginEntry baselib)
    {
        var installExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(installExe))
        {
            Console.Error.WriteLine("本体パスを特定できないため自己更新をスキップ。");
            return false;
        }

        // dotnet ミュクサ経由（dotnet <dll>）だと置換すべき本体 exe を特定できない。単一ファイル実行のみ対象。
        var exeName = Path.GetFileName(installExe);
        if (string.Equals(Path.GetFileNameWithoutExtension(exeName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "`dotnet <dll>` 経由の起動では本体を自己更新できない（単一ファイル発行の実行体で実行が必要）。スキップ。");
            return false;
        }

        // tmp 作業領域。本体・baselib を兄弟に置く（本体 csproj が baselib を相対参照するため）。
        var tmpRoot = Path.Combine(Path.GetTempPath(), "ai-harness-selfupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);

        var selfDir = Path.Combine(tmpRoot, PluginInstaller.RepoName(self.Path));
        var baselibDir = Path.Combine(tmpRoot, "ai-harness-baselib");
        Console.WriteLine($"clone: {self.Path} -> {selfDir}");
        PluginInstaller.CloneOrUpdate(self.Path, self.Branch, selfDir);
        Console.WriteLine($"clone: {baselib.Path} -> {baselibDir}");
        PluginInstaller.CloneOrUpdate(baselib.Path, baselib.Branch, baselibDir);

        // self-contained single-file で publish（配置先に .NET を要求しない現行方針に合わせる）。
        var csproj = PluginInstaller.FindCsproj(selfDir, "ai-harness-main");
        var outDir = Path.Combine(tmpRoot, "out");
        var rid = RuntimeInformation.RuntimeIdentifier;
        Console.WriteLine($"publish: {csproj} (rid={rid})");
        PluginInstaller.RunOrThrow("dotnet",
        [
            "publish", csproj, "-c", "Release", "-r", rid, "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", outDir,
        ]);

        var newExe = Path.Combine(outDir, exeName);
        if (!File.Exists(newExe))
        {
            throw new InvalidOperationException($"publish 出力に実行体が無い: {newExe}");
        }

        // 発行直後の健全性検証（壊れた本体で置換に進まない）。
        if (RunExe(newExe, ["--health"]) != 0)
        {
            throw new InvalidOperationException("publish した新バイナリの起動検証に失敗。");
        }

        // 新バイナリを applier として detached 起動。自身は即終了し、実行体ロックを解放する。
        StartDetached(newExe,
        [
            ApplyMode,
            "--target", installExe,
            "--pid", Environment.ProcessId.ToString(),
            "--tmp", tmpRoot,
        ]);
        return true;
    }

    /// <summary>
    /// <c>--apply-update</c> モード。tmp の新バイナリから動き、インストール先の実行体を安全に置換する。
    /// 引数: <c>--target &lt;install exe&gt; --pid &lt;旧プロセス&gt; --tmp &lt;作業領域&gt;</c>。
    /// </summary>
    public static int ApplyUpdate(string[] args)
    {
        var target = ArgValue(args, "--target");
        var pidText = ArgValue(args, "--pid");
        var tmpRoot = ArgValue(args, "--tmp");

        if (string.IsNullOrEmpty(target))
        {
            return 1; // 置換先不明。ログ先も定まらないため静かに失敗。
        }

        var installDir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var log = new Logger(LogLevel.Info, Path.Combine(installDir, "logs"), toStderr: false);
        log.Write(LogLevel.Info, $"自己更新 apply 開始 target={target}");

        var backup = target + ".bak";
        var pipe = HarnessPipe.NameFor(installDir);
        var wasRunning = Daemon.IsRunning(pipe);

        try
        {
            // 1. 旧 --update プロセスの終了を待つ（実行体ロック解放のため）。
            if (int.TryParse(pidText, out var pid))
            {
                WaitForProcessExit(pid, TimeSpan.FromSeconds(30));
            }

            // 2. daemon 停止 → 完全停止（ロック解放）まで待つ。
            if (wasRunning)
            {
                Daemon.Stop(pipe);
                for (var i = 0; i < 100 && Daemon.IsRunning(pipe); i++)
                {
                    Thread.Sleep(100);
                }
            }

            // 3. 現行実行体を退避。
            File.Copy(target, backup, overwrite: true);

            // 4. 新バイナリで上書き（ロック解放前は失敗し得るためリトライ）。
            CopyWithRetry(Environment.ProcessPath!, target, TimeSpan.FromSeconds(30));
            log.Write(LogLevel.Info, "実行体を置換。起動検証中。");

            // 5. 置換後の起動検証。失敗ならロールバック。
            if (RunExe(target, ["--health"]) != 0)
            {
                File.Copy(backup, target, overwrite: true);
                log.Write(LogLevel.Error, "置換後の起動検証に失敗。旧実行体へロールバックした。");
                RestartIfNeeded(target, wasRunning, log);
                return 1;
            }

            log.Write(LogLevel.Info, "自己更新に成功。");
            TryDelete(backup);
            RestartIfNeeded(target, wasRunning, log);
            return 0;
        }
        catch (Exception ex)
        {
            // 置換途中の失敗はロールバックを試みる。
            log.Write(LogLevel.Error, $"自己更新に失敗: {ex.Message}");
            try
            {
                if (File.Exists(backup))
                {
                    File.Copy(backup, target, overwrite: true);
                    log.Write(LogLevel.Info, "旧実行体へロールバックした。");
                }
            }
            catch (Exception rollbackEx)
            {
                log.Write(LogLevel.Error, $"ロールバックにも失敗: {rollbackEx.Message}");
            }
            RestartIfNeeded(target, wasRunning, log);
            return 1;
        }
        finally
        {
            // tmp 掃除（自分自身の exe は Windows で削除できないため best-effort）。
            if (!string.IsNullOrEmpty(tmpRoot))
            {
                TryDeleteDirectory(tmpRoot);
            }
        }
    }

    private static void RestartIfNeeded(string target, bool wasRunning, Logger log)
    {
        if (!wasRunning)
        {
            return;
        }
        try
        {
            // 置換後のインストール実行体で daemon を起動（新 lib と新バイナリを反映）。
            StartDetached(target, ["--ensure"]);
            log.Write(LogLevel.Info, "daemon を再起動した。");
        }
        catch (Exception ex)
        {
            log.Write(LogLevel.Error, $"daemon 再起動に失敗: {ex.Message}");
        }
    }

    // ---- ヘルパ ----

    private static string? ArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // 既に終了済み。
        }
    }

    private static void CopyWithRetry(string source, string dest, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                File.Copy(source, dest, overwrite: true);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // 置換対象がまだロック中（プロセス終了待ち）。少し待って再試行。
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static int RunExe(string file, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>親（Claude / bridge）の stdio から切り離して detached 起動する（<see cref="Daemon.StartDetached"/> と同方針）。</summary>
    private static void StartDetached(string exe, IReadOnlyList<string> args)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
        }
        else
        {
            psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 自分自身の exe を含む tmp は Windows で消せないことがある。無害。
        }
    }
}
