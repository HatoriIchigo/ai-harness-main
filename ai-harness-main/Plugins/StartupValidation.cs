namespace ai_harness_main;

/// <summary>
/// プロジェクトのプラグイン起動検証の結果。
///
/// <see cref="Errors"/> が空でなければ「<c>common.yml</c> で有効化したのに発火できる状態へ持ち込めなかった
/// プラグインがある」ことを意味する。そのガードは効かないため、フェイルクローズでそのプロジェクトの
/// hook をブロックする（検証できないアクションを素通りさせない）。設定を直せばホットリロードで解ける。
/// </summary>
/// <param name="ValidTypes">発火対象として残ったプラグイン型。</param>
/// <param name="Errors">有効化されたのに起動できなかったプラグインの理由（利用者向け）。</param>
internal sealed record StartupValidation(IReadOnlyList<Type> ValidTypes, IReadOnlyList<string> Errors)
{
    /// <summary>フェイルクローズすべきか。</summary>
    public bool IsFailClosed => Errors.Count > 0;

    /// <summary>Claude Code へ返す理由（全エラーを列挙）。</summary>
    public string Reason() =>
        "有効化されたプラグインを起動できませんでした（フェイルクローズ）。設定を修正してください:\n"
        + string.Join("\n", Errors.Select(e => $"- {e}"));
}
