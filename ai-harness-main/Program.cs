using System.Runtime.InteropServices;
using System.Text;
using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// ai-harness-main エントリポイント（単一バイナリ）。モードで分岐する。
///
///   （引数なし） … bridge。hook ごとに Claude Code が叩く受け口。stdin の hook JSON を読み、cwd から
///                  プロジェクトルートを解決し、封筒で daemon へ中継する。未起動なら daemon を起動。
///                  stdin が端末（人間が直接起動）または空なら hook 経由ではないため、読まずに使い方を
///                  出して 1 で終える（端末で入力待ちにならない）。
///   --daemon     … 常駐サーバー。名前付きパイプで待ち受け、プロジェクト別に処理。複数プロジェクト共有。
///   --ensure     … daemon 未起動なら detached 起動して終了。
///   --stop       … 稼働中の daemon を停止。
///   --restart    … daemon を停止してから再起動（lib＝プラグイン DLL の差し替え反映用）。
///   --standalone … daemon を介さず stdin を直接 1 件処理して終了（テスト・フォールバック）。
///   --update     … config/plugins.yml に従い拡張プラグインを repos/ へ clone／build し lib/ へ配置し、
///                  続けて本体自身も tmp へ publish して置換（自己更新）。git／dotnet 未導入なら異常終了（非 0）。
///   --update &lt;plugin name&gt; … その 1 プラグインのみ更新（clone／build／配置＋daemon 再起動）。本体自己更新はしない。
///   --apply-update … 内部モード（ユーザ非公開）。--update が publish した tmp の新バイナリから起動され、
///                  インストール先の実行体を安全に置換する。
///   --health     … 起動検証用。ランタイムが正常起動できれば 0 を返す（自己更新のロールバック判定に使う）。
///
/// 情報表示（人間向け。ハーネスの動作には影響しない）:
///
///   --project    … 稼働中の daemon がメモリに展開しているプロジェクト一覧。
///   --logs       … 実行体自身（daemon ライフサイクル）のログ。
///   --logs &lt;プロジェクト&gt; … そのプロジェクトのログ。
///   --plugin     … lib/ にインストール済みのプラグイン一覧。
///   --plugin &lt;プロジェクト&gt; … そのプロジェクトで有効化されているか。
///   --plugin [&lt;プロジェクト&gt;] --enable|--disable &lt;プラグイン名,…&gt; … そのプロジェクトの common.yml の
///                  tools を書き換えて有効化／無効化する（プロジェクト無指定は cwd から解決）。設定 YAML は
///                  ホットリロード対象のため daemon の再起動は要らない。
///   --fire       … cwd のプロジェクトで有効プラグインの能動スキャン（Fire）を daemon 経由で一斉起動。
///   --fire &lt;プラグイン名&gt; … そのプラグインだけ Fire を起動。
///                  終了コードは 0=問題なし / 2=いずれかが検出 / 1=接続・実行不能（hook 規約とは別系統）。
///   --lsp        … LSP の対応言語・候補サーバの一覧（daemon 不要）。
///   --lsp &lt;プロジェクト&gt; … そのプロジェクトの common.yml の lsp: 宣言と、daemon 上の実際の稼働状況
///                  （言語・サーバ・状態・エラー）。daemon 未起動／プロジェクト未展開でも「未起動」と表示するのみで、
///                  この照会のために daemon やプロジェクトを新規に起こすことはない。
///
///   --logs には <c>--n &lt;件数&gt;</c>（新しい順に上位 N 件）・<c>--filter &lt;レベル,…&gt;</c>（レベル絞り込み）・
///   <c>--deny</c>（deny の監査レコードのみ）を付けられる。
///
///   --help / -h  … 使い方を表示。
///
/// bridge になるのは<b>引数なし</b>のときだけ。未知の引数は使い方を出して 1 で終わる（typo を
/// hook として扱わない）。
///
/// 終了コード（bridge / standalone の Claude hook 規約）: 0=許可 / 2=deny。
/// 内部エラー・不正入力・検証不能は**フェイルクローズ**で 2（ブロック）に倒す。例外は bridge が daemon に
/// まったく接続できない場合のみで、ロックアウト回避のため 0（許可）で継続する。
/// 情報表示モードは hook 規約の外なので、成功 0 / 引数エラー 1 を返す。
/// bridge が hook 入力そのものを受け取っていない（stdin が端末・空）ときも、ツールの許可／deny の判断では
/// なく叩き方の誤りなので、hook 規約の外として 1 を返す。
/// </summary>
public static class Program
{
    private const int ExitAllow = 0;
    private const int ExitDeny = 2;
    private const int ExitUsage = 1;

    public static async Task<int> Main(string[] args)
    {
        // 出力の読み手（端末／ai-harness-tui／Claude Code）は一様に UTF-8 を前提とするため、
        // モードに依らず最初に固定する。
        UseUtf8Output();

        var mode = args.Length > 0 ? args[0] : null;

        switch (mode)
        {
            case "--daemon":
            {
                PreloadNativeLibraries();
                // 常駐時は stderr に消費者がいないためファイルのみへ出力（グローバル log）。
                var logger = new Logger(LogLevel.Info, InstallPaths.GlobalLogDir, toStderr: false);
                return await Daemon.RunAsync(logger).ConfigureAwait(false);
            }

            case "--ensure":
                return Daemon.Ensure();

            case "--stop":
                return Daemon.Stop();

            case "--restart":
                return Daemon.Restart();

            case "--standalone":
                return await RunStandaloneAsync().ConfigureAwait(false);

            case "--update":
            {
                // 位置引数は更新対象プラグイン名 1 個（省略時は全プラグイン＋本体自己更新）。
                if (!TryParseSingleOptionalName(args, "プラグイン", out var pluginName, out var updateError))
                {
                    await Console.Error.WriteLineAsync(updateError).ConfigureAwait(false);
                    await Console.Error.WriteLineAsync(Usage.Text).ConfigureAwait(false);
                    return ExitUsage;
                }
                return PluginInstaller.Run(pluginName);
            }

            case "--apply-update":
                return SelfUpdater.ApplyUpdate(args);

            case "--health":
                Console.WriteLine("ai-harness-main OK");
                return ExitAllow;

            case "--doctor":
            case "--project":
            case "--logs":
            case "--plugin":
            case "--validate":
            case "--lsp":
            {
                if (!CliOptions.TryParse(args, out var options, out var error))
                {
                    await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
                    return ExitUsage;
                }
                return await RunInfoAsync(mode, options).ConfigureAwait(false);
            }

            case "--fire":
            {
                // 位置引数は「プラグイン名」1 個（情報表示系の「プロジェクトルート」とは別物ゆえ個別に解釈）。
                if (!TryParseSingleOptionalName(args, "プラグイン", out var pluginName, out var fireError))
                {
                    await Console.Error.WriteLineAsync(fireError).ConfigureAwait(false);
                    await Console.Error.WriteLineAsync(Usage.Text).ConfigureAwait(false);
                    return ExitUsage;
                }
                return await FireCommand.RunAsync(pluginName).ConfigureAwait(false);
            }

            case "--version":
            case "-v":
                return VersionCommand.Run();

            case "--help":
            case "-h":
                Console.Out.WriteLine(Usage.Text);
                return ExitAllow;

            case null:
                // 引数なしのときだけ bridge。Claude Code の hook はこの形で叩く。
                return await Bridge.RunAsync().ConfigureAwait(false);

            default:
                // 未知の引数を bridge に落とすと、端末では stdin 待ちで固まり、空 stdin では
                // 「hookJson が空」というフェイルクローズになる。typo を hook として扱わない。
                await Console.Error.WriteLineAsync($"不明な引数: {mode}").ConfigureAwait(false);
                await Console.Error.WriteLineAsync(Usage.Text).ConfigureAwait(false);
                return ExitUsage;
        }
    }

    /// <summary>
    /// 位置引数を任意の名前 1 個（<c>--fire</c> / <c>--update</c> のプラグイン名）として解釈する。
    /// 未指定は <paramref name="name"/> が null。未知のオプションや複数の位置引数は <paramref name="error"/> を
    /// 立てて <c>false</c>。<paramref name="itemLabel"/> はエラー文言に使う語（例: 「プラグイン」）。
    /// </summary>
    private static bool TryParseSingleOptionalName(
        string[] args, string itemLabel, out string? name, out string error)
    {
        name = null;
        error = "";
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith('-'))
            {
                error = $"不明なオプション: {arg}";
                return false;
            }
            if (name is not null)
            {
                error = $"{itemLabel}は 1 つだけ指定してください: {name} / {arg}";
                return false;
            }
            name = arg;
        }
        return true;
    }

    /// <summary>情報表示モードを実行する。<c>--project</c> は位置引数・オプションを取らない。</summary>
    private static async Task<int> RunInfoAsync(string mode, CliOptions options)
    {
        // 引数をまったく取らないモード。
        if (mode is "--project" or "--doctor")
        {
            if (options.Project is not null || options.Take is not null
                || options.Levels is not null || options.DenyOnly || options.Toggles.Count > 0)
            {
                await Console.Error.WriteLineAsync($"{mode} は引数を取りません。").ConfigureAwait(false);
                return ExitUsage;
            }
            return mode == "--doctor"
                ? await DoctorCommand.RunAsync().ConfigureAwait(false)
                : await ProjectsCommand.RunAsync().ConfigureAwait(false);
        }

        // 設定の書き換えは --plugin の責務。他のモードでは受け付けない。
        if (mode != "--plugin" && options.Toggles.Count > 0)
        {
            await Console.Error.WriteLineAsync($"{mode} は --enable / --disable を取りません。").ConfigureAwait(false);
            return ExitUsage;
        }

        if (mode == "--logs")
        {
            return LogsCommand.Run(options);
        }

        if (options.Take is not null || options.Levels is not null || options.DenyOnly)
        {
            await Console.Error.WriteLineAsync($"{mode} は --n / --filter / --deny を取りません。").ConfigureAwait(false);
            return ExitUsage;
        }
        if (mode == "--lsp")
        {
            return await LspCommand.RunAsync(options.Project).ConfigureAwait(false);
        }
        return mode == "--validate" ? ValidateCommand.Run(options) : PluginsCommand.Run(options);
    }

    /// <summary>
    /// stdout／stderr を UTF-8（BOM なし）にする。全モードで呼ぶ。既定では Windows のコンソール
    /// コードページ（cp932 等）で書かれ、読む側が UTF-8 を前提とするため日本語が化ける。
    /// 読み手は情報表示モードなら端末・パイプ（ai-harness-tui）、hook 経路（bridge／standalone）なら
    /// Claude Code で、いずれも UTF-8 を期待する。Linux は既定が UTF-8 のため実質 Windows 向けの是正。
    ///
    /// <see cref="Console.SetOut"/>／<see cref="Console.SetError"/> で UTF-8 のライタを据えるのが本体。
    /// <see cref="Console.OutputEncoding"/> はコンソールハンドルが無い（hook のようにリダイレクトされた）
    /// 環境では設定できないため、端末表示を整える補助として試すだけに留める。
    /// </summary>
    internal static void UseUtf8Output()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            Console.OutputEncoding = utf8;
        }
        catch (IOException)
        {
            // コンソールハンドルが無い環境では設定できない。以下のライタ差し替えは効くため続行する。
        }
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    }

    /// <summary>
    /// 実行体隣の <c>runtimes/&lt;rid&gt;/native</c> にあるネイティブライブラリをフルパスで事前ロードする。
    ///
    /// tree-sitter を使うプラグイン（constants／file-rules／outside-deny）が依存する TreeSitter.DotNet は、grammar を
    /// <c>NativeLibrary.Load(ベア名)</c> で読み込む。これは ALC のネイティブ解決フックも <c>.deps.json</c>
    /// リゾルバも通らず、OS 既定探索（実行体ディレクトリ・system・PATH）だけが効くため、それらに含まれない
    /// 配置では <c>DllNotFoundException</c> になる。先にフルパスでロードしておけば、以降のベア名ロードは
    /// OS が既ロードのモジュールを返す。PATH や探索パスを一切変更せず、アプリが自分の同梱ネイティブを
    /// 明示的にロードするだけ。プラグインを動かすモード（daemon／standalone）で発火前に一度呼ぶ。
    ///
    /// <b>これだけでは足りない</b>。既ロードの再利用は <b>SONAME</b> で照合されるため、ファイル名と SONAME が
    /// 食い違う場合（実測: <c>libtree-sitter.so</c> の SONAME は <c>libtree-sitter.so.0.26</c>）、事前ロードしても
    /// <c>DllImport("tree-sitter")</c> は既ロードに当たらない。<c>DllImport</c> 経由の解決は
    /// <see cref="PluginLoadContext"/> が <c>runtimes/&lt;rid&gt;/native</c> を直接プローブして担う（2 経路で補完し合う）。
    ///
    /// この実行体隣の <c>runtimes/</c> に**既定リリースで置いてよいのは tree-sitter の native のみ**（汎用
    /// first-party の特例）。それ以外で native が要るプラグインは、native を**自分の管理 DLL に埋め込み**、
    /// host が起動時に <c>runtimes/&lt;rid&gt;/native/</c> へ**自動展開**（冪等・グローバル単一・起動時 1 回）して
    /// から本メソッドで事前ロードする。使用者は <c>lib/</c> に管理 DLL を置くだけで <c>runtimes/</c> を触らない。
    /// この自動展開フックは最初の非 tree-sitter native プラグイン登場時に実装する。詳細は
    /// docs/build-and-deploy.md の「native 配布ポリシー」を参照。
    ///
    /// 個々のロード失敗はここでは無視して daemon 起動自体は止めない。ただし当該 grammar を使う tree-sitter
    /// プラグインは実行時に AST 解析へ失敗し、フェイルクローズで当該アクションがブロックされる。
    /// </summary>
    private static void PreloadNativeLibraries()
    {
        var nativeDir = Path.Combine(
            AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        if (!Directory.Exists(nativeDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(nativeDir))
        {
            try
            {
                NativeLibrary.Load(file);
            }
            catch
            {
                // 個別の解決失敗はブロックしない（当該プラグインが素通りするだけ）。
            }
        }
    }

    /// <summary>daemon を介さず stdin を直接 1 件処理する（テスト・フォールバック用）。</summary>
    private static async Task<int> RunStandaloneAsync()
    {
        PreloadNativeLibraries();
        var projectRoot = ProjectLocator.Resolve(Environment.CurrentDirectory);

        HookData data;
        try
        {
            await using var stdin = Console.OpenStandardInput();
            data = await HookData.ParseAsync(stdin).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // フェイルクローズ: 解釈できない入力は通さない（daemon 経路と揃える）。
            await Console.Error.WriteLineAsync(
                $"hook データの解析に失敗（フェイルクローズ）: {ex.Message}").ConfigureAwait(false);
            return ExitDeny;
        }

        HostDecision decision;
        ProjectContext? ctx = null;
        try
        {
            var registry = new PluginRegistry(_ => { }, InstallPaths.LibDir);
            ctx = ProjectContext.Create(registry, _ => { }, projectRoot);
            decision = await ctx.RunAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // フェイルクローズ: 検証できなかった場合はブロックする。
            await Console.Error.WriteLineAsync($"処理に失敗（フェイルクローズ）: {ex.Message}").ConfigureAwait(false);
            return ExitDeny;
        }
        finally
        {
            ctx?.Dispose();
        }

        if (decision.IsDeny)
        {
            if (!string.IsNullOrWhiteSpace(decision.Reason))
            {
                await Console.Error.WriteLineAsync(decision.Reason).ConfigureAwait(false);
            }
            return ExitDeny;
        }
        // 非ブロックのコンテキスト注入。standalone は自身で hook 出力 JSON を stdout へ。
        if (!string.IsNullOrEmpty(decision.AdditionalContext) && !string.IsNullOrEmpty(data.HookEventName))
        {
            Console.Out.Write(HookOutput.BuildAdditionalContext(data.HookEventName, decision.AdditionalContext));
        }
        return ExitAllow;
    }
}
