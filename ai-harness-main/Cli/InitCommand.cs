namespace ai_harness_main;

/// <summary>
/// <c>--init [プロジェクト] [--enable 名,…]</c>: 新規／既存プロジェクトへのハーネス配線を自動化する。
///
/// <list type="number">
///   <item><c>.claude/settings.json</c> に <c>ai-harness-main</c> の <c>PreToolUse</c>／<c>PostToolUse</c>
///     hook を追記する（<see cref="SettingsJsonEditor"/>。既に配線済みなら変更しない）。</item>
///   <item>有効化するプラグインを選ぶ。<c>--enable</c> があればそれを使い、無ければ <c>lib/</c> の
///     インストール済み一覧から対話的に選ばせる（矢印キーまたは j/k で移動・space で選択切替・
///     Enter で確定。標準入出力がリダイレクトされている場合はカンマ区切りの番号入力にフォールバックする）。</item>
///   <item>選んだプラグインを <c>common.yml</c> の <c>tools</c> へ書き込む。発見・デフォルト設定配置・
///     フェイルクローズ検証・書き込みは <c>--plugin --enable</c>（<see cref="PluginsCommand"/>）と
///     完全に同じ経路を通す（二重実装しない）。</item>
/// </list>
///
/// プロジェクト無指定は cwd から解決する（<c>.claude</c> が無ければ cwd 自体を新規プロジェクトルートとする）。
/// 選択したプラグインが 0 件なら <c>common.yml</c> には触れない（settings.json の配線だけで終える）。
/// </summary>
internal static class InitCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        if (!ProjectPath.TryResolveOrLocate(options.Project, out var root, out var resolveError))
        {
            await Console.Error.WriteLineAsync(resolveError).ConfigureAwait(false);
            return 1;
        }
        if (options.Toggles.Any(t => !t.Enable))
        {
            await Console.Error.WriteLineAsync(
                "--init は --disable を受け付けません（--enable のみ）。").ConfigureAwait(false);
            return 1;
        }

        Console.Out.WriteLine($"project: {root}");
        Console.Out.WriteLine();

        var (settingsChanged, settingsError) = SettingsJsonEditor.EnsureHooks(root);
        if (settingsError is not null)
        {
            await Console.Error.WriteLineAsync(settingsError).ConfigureAwait(false);
            return 1;
        }
        Console.Out.WriteLine(settingsChanged
            ? "settings.json: ai-harness-main の hook を追加しました（PreToolUse／PostToolUse）。"
            : "settings.json: 既に ai-harness-main が配線済みです（変更なし）。");

        var (registry, plugins) = PluginsCommand.Discover();
        if (plugins.Count == 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("lib/ にインストール済みのプラグインが無いため、有効化は行いません。");
            Console.Out.WriteLine("ai-harness-main --update でプラグインを導入してから、改めて --plugin --enable してください。");
            return 0;
        }

        var selected = options.Toggles.Count > 0
            ? options.Toggles.Select(t => t.Name).ToList()
            : PromptSelection(plugins);

        if (selected.Count == 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("有効化するプラグインが選ばれなかったため、common.yml は変更しません。");
            return 0;
        }

        var config = ProjectConfig.Load(root, out var warning);
        if (warning is not null)
        {
            await Console.Error.WriteLineAsync(warning).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                $"{ProjectConfig.ConfigFileName} を直してから再実行してください。").ConfigureAwait(false);
            return 1;
        }

        var toggles = selected.Select(name => (Name: name, Enable: true)).ToList();

        // 有効化するプラグインの設定 YAML が無ければデフォルトを配置してから、フェイルクローズを検証する
        // （--plugin --enable と同じ手順。詳細は PluginsCommand の該当メソッドを参照）。
        PluginsCommand.EnsureDefaultConfigs(toggles, config.ConfigDir, registry);

        if (!PluginsCommand.CanEnable(toggles, config, registry, out var blockers))
        {
            Console.Out.WriteLine();
            await Console.Error.WriteLineAsync(
                "有効化を中止しました（この設定では hook が全て deny されます）:").ConfigureAwait(false);
            foreach (var blocker in blockers)
            {
                await Console.Error.WriteLineAsync($"- {blocker}").ConfigureAwait(false);
            }
            return 1;
        }

        if (!CommonYamlEditor.TryApply(
                config.ConfigFilePath, toggles, out var results, out var created, out var editError))
        {
            await Console.Error.WriteLineAsync(editError).ConfigureAwait(false);
            return 1;
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(
            $"common.yml: {Path.GetFullPath(config.ConfigFilePath)}{(created ? "（新規作成）" : "")}");
        foreach (var result in results)
        {
            Console.Out.WriteLine($"  有効化: {result.PluginName}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine("初期化が完了しました。Claude Code を再起動すると hook の配線が反映されます。");
        return 0;
    }

    /// <summary>
    /// インストール済みプラグインを対話的に選ばせる。標準入出力が端末なら矢印キー／space のチェックボックス
    /// UI（<see cref="PromptSelectionInteractive"/>）、リダイレクトされている（パイプ・自動化）なら
    /// <see cref="Console.ReadKey()"/> が使えないため、カンマ区切りの番号入力にフォールバックする。
    /// </summary>
    private static List<string> PromptSelection(IReadOnlyList<PluginsCommand.PluginInfo> plugins)
    {
        Console.Out.WriteLine();
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return PromptSelectionByLine(plugins);
        }

        try
        {
            return PromptSelectionInteractive(plugins);
        }
        catch (InvalidOperationException)
        {
            // 端末なのに ReadKey が使えない環境（一部の CI 端末等）。行入力へ切り替える。
            return PromptSelectionByLine(plugins);
        }
    }

    /// <summary>
    /// 矢印キー（↑/↓）または vim 風の j/k（j=下・k=上）でカーソルを移動し、space で選択を切り替え、
    /// Enter で確定するチェックボックス選択 UI。Esc／q は「選択なし」で中止。
    /// </summary>
    private static List<string> PromptSelectionInteractive(IReadOnlyList<PluginsCommand.PluginInfo> plugins)
    {
        var selected = new bool[plugins.Count];
        var cursor = 0;

        Console.Out.WriteLine("有効化するプラグインを選んでください（↑/↓ か j/k で移動、space 切替、Enter 確定、q 中止）:");
        RenderSelection(plugins, selected, cursor, redraw: false);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.K:
                    cursor = (cursor - 1 + plugins.Count) % plugins.Count;
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.J:
                    cursor = (cursor + 1) % plugins.Count;
                    break;
                case ConsoleKey.Spacebar:
                    selected[cursor] = !selected[cursor];
                    break;
                case ConsoleKey.Enter:
                    // 直前の描画がそのまま最後の行になっているため、カーソルの巻き戻しは不要。
                    return plugins.Where((_, i) => selected[i]).Select(p => p.Name).ToList();
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    return [];
            }
            RenderSelection(plugins, selected, cursor, redraw: true);
        }
    }

    /// <summary>
    /// プラグイン一覧を描画する。<paramref name="redraw"/> が <c>true</c> なら、直前にこのメソッドが
    /// 書いた <c>plugins.Count</c> 行ぶんを「現在のカーソル位置からの相対移動」で巻き戻してから上書きする。
    ///
    /// 固定の絶対行番号（一度だけ取得した <c>Console.CursorTop</c>）を使い回す実装は、Windows Terminal／
    /// VS Code 統合ターミナル等（ConPTY 系）で画面がスクロールした瞬間に無効な行番号になり
    /// <see cref="ArgumentOutOfRangeException"/> で落ちる（バッファ高がウィンドウ高と同じで、
    /// 「スクロール前の絶対位置」が「スクロール後」には存在しないため）。<see cref="Console.CursorTop"/> を
    /// 毎回その場で読み直し、そこからの相対移動だけで巻き戻すことでこの問題を避ける。
    /// </summary>
    private static void RenderSelection(
        IReadOnlyList<PluginsCommand.PluginInfo> plugins, bool[] selected, int cursor, bool redraw)
    {
        if (redraw)
        {
            // 極端に小さい端末（プラグイン数より画面が低い）で負値にならないよう下限を 0 に丸める。
            var rewound = Math.Max(0, Console.CursorTop - plugins.Count);
            Console.SetCursorPosition(0, rewound);
        }
        for (var i = 0; i < plugins.Count; i++)
        {
            var pointer = i == cursor ? ">" : " ";
            var checkbox = selected[i] ? "[x]" : "[ ]";
            var description = string.IsNullOrWhiteSpace(plugins[i].Description) ? "-" : plugins[i].Description;
            Console.Out.WriteLine($"{pointer} {checkbox} {plugins[i].Name} - {description}");
        }
    }

    /// <summary>
    /// インストール済みプラグインの番号付き一覧を出し、標準入力からカンマ区切りの選択を読む
    /// （<see cref="PromptSelectionInteractive"/> が使えない環境向けのフォールバック）。
    /// 空入力は「選択なし」、<c>all</c> は全選択。不正な番号は無視して警告する。
    /// </summary>
    private static List<string> PromptSelectionByLine(IReadOnlyList<PluginsCommand.PluginInfo> plugins)
    {
        Console.Out.WriteLine("有効化するプラグインを選んでください:");
        for (var i = 0; i < plugins.Count; i++)
        {
            var description = string.IsNullOrWhiteSpace(plugins[i].Description) ? "-" : plugins[i].Description;
            Console.Out.WriteLine($"  {i + 1}) {plugins[i].Name} - {description}");
        }
        Console.Out.WriteLine("番号をカンマ区切りで入力してください（例: 1,3）。'all' で全選択、空欄で選択なし。");
        Console.Out.Write("> ");

        var line = Console.In.ReadLine();
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }
        if (line.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return plugins.Select(p => p.Name).ToList();
        }

        var selected = new List<string>();
        foreach (var token in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var index) && index >= 1 && index <= plugins.Count)
            {
                var name = plugins[index - 1].Name;
                if (!selected.Contains(name))
                {
                    selected.Add(name);
                }
            }
            else
            {
                Console.Error.WriteLine($"無視: 不正な番号 '{token}'");
            }
        }
        return selected;
    }
}
