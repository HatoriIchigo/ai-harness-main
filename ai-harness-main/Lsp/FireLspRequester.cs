using ai_harness_baselib;

namespace ai_harness_main;

/// <summary><see cref="IFireLspRequester"/> の実体。<see cref="LspManager.RequestDiagnosticsSync"/> への薄いラッパー。</summary>
internal sealed class FireLspRequester(string projectRoot) : IFireLspRequester
{
    public IReadOnlyList<LspDiagnostic> RequestDiagnostics(string filePath, string content, TimeSpan timeout) =>
        LspManager.RequestDiagnosticsSync(projectRoot, filePath, content, timeout);
}
