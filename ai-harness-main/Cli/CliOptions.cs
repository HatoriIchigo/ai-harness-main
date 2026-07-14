using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 情報表示系サブコマンド（<c>--project</c> / <c>--logs</c> / <c>--plugin</c>）の引数。
///
/// <code>
///   --logs   [プロジェクトルート] [--n 件数] [--filter warn,debug] [--deny]
///   --plugin [プロジェクトルート] [--enable 名,…] [--disable 名,…]
///   --project
/// </code>
///
/// 位置引数はプロジェクトルート 1 個のみ。<c>--filter</c> はレベル名として解釈できる語だけを食べるので、
/// <c>--filter warn, debug C:\proj</c> のようにパスが後続しても取り違えない。
/// </summary>
internal sealed class CliOptions
{
    /// <summary>対象プロジェクトルート（位置引数）。未指定は <c>null</c>。</summary>
    public string? Project { get; private init; }

    /// <summary>表示件数の上限（<c>--n</c>）。未指定は <c>null</c>＝全件。</summary>
    public int? Take { get; private init; }

    /// <summary>表示対象のログレベル（<c>--filter</c>）。未指定は <c>null</c>＝全レベル。</summary>
    public IReadOnlySet<LogLevel>? Levels { get; private init; }

    /// <summary>deny の監査レコードだけを表示する（<c>--deny</c>）。</summary>
    public bool DenyOnly { get; private init; }

    /// <summary>
    /// 有効化／無効化するプラグイン（<c>--enable</c> / <c>--disable</c>、指定順）。
    /// 未指定は空＝<c>--plugin</c> は表示のみ。
    /// </summary>
    public IReadOnlyList<(string Name, bool Enable)> Toggles { get; private init; } = [];

    /// <summary>
    /// <paramref name="args"/> の 1 番目以降（0 番目はモード名）を解釈する。
    /// 失敗時は <paramref name="error"/> に利用者向けの理由を入れて <c>false</c>。
    /// </summary>
    public static bool TryParse(string[] args, out CliOptions options, out string error)
    {
        string? project = null;
        int? take = null;
        HashSet<LogLevel>? levels = null;
        var denyOnly = false;
        var toggles = new List<(string Name, bool Enable)>();
        error = "";
        options = new CliOptions();

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--n":
                case "-n":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var n) || n <= 0)
                    {
                        error = $"{arg} には 1 以上の整数を指定してください。";
                        return false;
                    }
                    take = n;
                    break;

                case "--deny":
                    denyOnly = true;
                    break;

                case "--enable":
                case "--disable":
                {
                    var enable = arg == "--enable";
                    if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                    {
                        error = $"{arg} にはプラグイン名を指定してください（カンマ区切りで複数可）。";
                        return false;
                    }
                    var names = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (names.Length == 0)
                    {
                        error = $"{arg} にはプラグイン名を指定してください（カンマ区切りで複数可）。";
                        return false;
                    }
                    foreach (var name in names)
                    {
                        // 同じプラグインに有効化と無効化を同時指定＝どちらが意図か決められない。
                        if (toggles.Any(t => t.Name == name && t.Enable != enable))
                        {
                            error = $"同じプラグインを --enable と --disable の両方に指定しています: {name}";
                            return false;
                        }
                        if (toggles.Any(t => t.Name == name))
                        {
                            continue; // 同じ向きの重複指定は無視
                        }
                        toggles.Add((name, enable));
                    }
                    break;
                }

                case "--filter":
                    levels = [];
                    while (i + 1 < args.Length && TryParseLevels(args[i + 1], out var parsed))
                    {
                        levels.UnionWith(parsed);
                        i++;
                    }
                    if (levels.Count == 0)
                    {
                        error = "--filter にはログレベル（trace/debug/info/warn/error）を指定してください。";
                        return false;
                    }
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"不明なオプション: {arg}";
                        return false;
                    }
                    if (project is not null)
                    {
                        error = $"プロジェクトは 1 つだけ指定してください: {project} / {arg}";
                        return false;
                    }
                    project = arg;
                    break;
            }
        }

        options = new CliOptions
        {
            Project = project,
            Take = take,
            Levels = levels,
            DenyOnly = denyOnly,
            Toggles = toggles,
        };
        return true;
    }

    /// <summary>
    /// <c>warn,debug</c> / <c>warn,</c> / <c>warn</c> のような 1 トークンをレベル集合に解釈する。
    /// 1 要素でも未知の語があれば <c>false</c>（＝この語は <c>--filter</c> の続きではない）。
    /// </summary>
    private static bool TryParseLevels(string token, out List<LogLevel> levels)
    {
        levels = [];
        var parts = token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }
        foreach (var part in parts)
        {
            if (!LogLevels.TryParse(part, out var level))
            {
                return false;
            }
            levels.Add(level);
        }
        return true;
    }
}
