namespace ai_harness_main;

/// <summary>診断 1 項目の結果。</summary>
internal enum DoctorStatus
{
    /// <summary>問題なし。</summary>
    Ok,

    /// <summary>一部機能が使えないが、ハーネスの中核は動く。</summary>
    Warn,

    /// <summary>ハーネスが正しく機能しない。</summary>
    Error,
}

/// <summary>診断 1 項目。</summary>
/// <param name="Name">項目名。</param>
/// <param name="Status">判定。</param>
/// <param name="Detail">根拠（パス・件数・失敗理由など）。</param>
internal readonly record struct DoctorCheck(string Name, DoctorStatus Status, string Detail)
{
    public static DoctorCheck Ok(string name, string detail) => new(name, DoctorStatus.Ok, detail);

    public static DoctorCheck Warn(string name, string detail) => new(name, DoctorStatus.Warn, detail);

    public static DoctorCheck Error(string name, string detail) => new(name, DoctorStatus.Error, detail);

    /// <summary>表示用の短い status 名。</summary>
    public string StatusText => Status switch
    {
        DoctorStatus.Error => "error",
        DoctorStatus.Warn => "warn",
        _ => "ok",
    };
}
