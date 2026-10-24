using ImanSoftware.Extensions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Resolves model-supplied paths safely: workspace-relative only, no <c>..</c>, no absolute paths,
/// nothing under <c>.git</c> or <c>.agent</c>, and no escape through a symlink.
/// </summary>
internal sealed class PathSandbox : IPathSandbox
{
    private readonly string _workspaceRoot;

    public PathSandbox(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        _workspaceRoot = root;
    }

    internal string WorkspaceRoot => _workspaceRoot;

    /// <inheritdoc />
    public Outcome<string> Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Outcome.Failure<string>(new OutcomeError(
                "The path is empty.", ErrorCodes.PathOutsideWorkspace, OutcomeErrorType.Validation));
        }

        var normalized = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (relativePath.Contains('\\') ||
            Path.IsPathRooted(normalized) ||
            normalized.Split(Path.DirectorySeparatorChar).Any(s => s is ".." or "."))
        {
            return Reject(relativePath, "absolute paths and \"..\" are not allowed");
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_workspaceRoot, normalized));
        }
        catch (Exception ex)
        {
            return Outcome.Failure<string>(new OutcomeError(ex));
        }

        var rootWithSeparator = _workspaceRoot;

        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            return Reject(relativePath, "the resolved path is outside the workspace");

        var relativeUnderRoot = full[rootWithSeparator.Length..];
        var firstSegment = relativeUnderRoot.Split(Path.DirectorySeparatorChar)[0];
        if (firstSegment is ".git" or ".agent")
            return Reject(relativePath, $"paths under \"{firstSegment}\" are not allowed");

        if (EscapesThroughSymlink(full, rootWithSeparator))
            return Reject(relativePath, "the path goes through a symlink pointing outside the workspace");

        return Outcome.Successful(full);
    }

    private static bool EscapesThroughSymlink(string full, string root)
    {
        try
        {
            var directory = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(directory) &&
                   directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.ResolveLinkTarget(directory, returnFinalTarget: true) is { } target &&
                    !target.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var parent = Path.GetDirectoryName(directory);
                if (parent is null || parent.Length < root.Length)
                    break;
                directory = parent;
            }

            if (File.Exists(full) &&
                File.ResolveLinkTarget(full, returnFinalTarget: true) is { } fileTarget &&
                !fileTarget.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // A path that cannot be inspected is treated as unsafe.
            return true;
        }

        return false;
    }

    private static Outcome<string> Reject(string relativePath, string reason)
        => Outcome.Failure<string>(new OutcomeError(
            $"Path \"{relativePath.Truncate(120)}\" rejected: {reason}.",
            ErrorCodes.PathOutsideWorkspace, OutcomeErrorType.Validation));
}
