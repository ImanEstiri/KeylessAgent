using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// The path safety gate: every model-supplied path goes through here before any file operation.
/// </summary>
public interface IPathSandbox : IService
{
    /// <summary>
    /// Resolves a workspace-relative path to an absolute path and verifies it stays inside the workspace,
    /// outside <c>.git</c> and <c>.agent</c>, and does not escape through <c>..</c> or a symlink.
    /// </summary>
    /// <param name="relativePath">The model-supplied path (forward slashes).</param>
    /// <returns>The absolute path, or a <c>KA_PATH_OUTSIDE</c> failure.</returns>
    Outcome<string> Resolve(string relativePath);
}
