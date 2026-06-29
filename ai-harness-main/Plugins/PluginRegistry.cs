using ai_harness_baselib;

namespace ai_harness_main;

/// <summary>
/// 共有プラグインの型レジストリ。daemon 起動時に <c>lib/</c> から <see cref="PluginBase"/> 派生型を
/// <b>1 回だけ</b>発見して保持する。型はプロセス寿命にわたり全プロジェクトで共有される。
///
/// プロジェクトごとの有効化（toggle）・設定ロード・<see cref="PluginBase.Init"/>・発火は
/// <see cref="ProjectContext"/> 側がこの型一覧を基に行う（lib は共通・config は個別）。
/// DLL の差し替え反映は <c>--restart</c>（このレジストリの再構築）で行い、ホットリロード対象外。
/// </summary>
internal sealed class PluginRegistry
{
    public PluginRegistry(Action<LogEntry> log, string libDir)
    {
        Types = new PluginLoader(log).DiscoverTypes(libDir);
    }

    /// <summary>発見済みの具象プラグイン型一覧（全プロジェクト共有）。</summary>
    public IReadOnlyList<Type> Types { get; }

    /// <summary>発見した型数。</summary>
    public int Count => Types.Count;
}
