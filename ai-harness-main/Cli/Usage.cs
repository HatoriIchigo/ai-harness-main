namespace ai_harness_main;

/// <summary>
/// <c>--help</c> と「不明な引数」エラーで出す使い方。
/// 内部モード（<c>--apply-update</c>）は利用者が直接叩くものではないため載せない。
/// </summary>
internal static class Usage
{
    public const string Text = """
        使い方: ai-harness-main [モード] [オプション]

          （引数なし）  hook の受け口（bridge）。stdin の hook JSON を daemon へ中継する。
                        Claude Code の settings.json から呼ばれる形。

        daemon 制御:
          --daemon                  常駐サーバーとして起動する
          --ensure                  未起動なら detached 起動する
          --restart                 停止してから起動し直す（lib の DLL 差し替えを反映）
          --stop                    稼働中の daemon を停止する
          --standalone              daemon を介さず stdin を 1 件処理する

        更新:
          --update                  プラグインと本体を更新する
          --health                  起動検証（ランタイムが正常起動すれば 0）

        検証:
          --validate [プロジェクト] 設定で hook が通る状態か確かめる（無指定は cwd から解決）。
                                    0=成功 / 1=失敗。daemon に触れずログも書かない
          --doctor                  この配置でハーネスが機能するか診断する（lib・native・daemon・git/dotnet）

        情報表示:
          --project                 daemon がメモリに展開しているプロジェクト一覧
          --logs   [プロジェクト]   ログを新しい順に表示（無指定は実行体自身のログ）
          --plugin [プロジェクト]   プラグイン一覧（無指定）／有効状態（指定時）

        スキャン:
          --fire   [プラグイン名]   cwd のプロジェクトで有効プラグインの能動スキャン（Fire）を
                                    daemon 経由で起動する（無指定は全プラグイン）。未起動なら起動する。
                                    0=問題なし / 2=検出 / 1=接続・実行不能

          --logs のオプション:
            --n <件数>              上位 N 件（新しい順）
            --filter <レベル,…>     trace / debug / info / warn / error
            --deny                  deny の監査レコードのみ

        その他:
          --version, -v             版・ランタイム・実行体パスを表示する
          --help, -h                この使い方を表示する
        """;
}
