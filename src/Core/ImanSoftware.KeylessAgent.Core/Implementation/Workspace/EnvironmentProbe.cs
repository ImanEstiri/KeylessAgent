using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Workspace;

/// <summary>
/// Probes the environment once at session start (in parallel, 3s timeout each) and produces
/// a compact block for the first chat message plus a one-line summary for reminders.
/// </summary>
internal sealed class EnvironmentProbe
{
    private readonly ProcessRunner _runner;

    public EnvironmentProbe()
        : this(new ProcessRunner())
    {
    }

    internal EnvironmentProbe(ProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    /// Probes OS, shell and tool versions in parallel (3s timeout each).
    /// </summary>
    /// <param name="workspaceRoot">The workspace (working directory for version probes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A multi-line environment block and a one-line summary.</returns>
    public async Task<Outcome<(string Block, string Summary)>> ProbeAsync(
        string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var osTask = RunAsync("cmd", "/c ver", workspaceRoot, cancellationToken);
        var gitTask = RunAsync("git", "--version", workspaceRoot, cancellationToken);
        var dotnetTask = RunAsync("dotnet", "--version", workspaceRoot, cancellationToken);
        var nodeTask = RunAsync("node", "-v", workspaceRoot, cancellationToken);
        var npmTask = RunAsync("npm", "-v", workspaceRoot, cancellationToken);
        var pythonTask = RunAsync("python", "--version", workspaceRoot, cancellationToken);

        string?[] results;
        try
        {
            results = await Task.WhenAll(osTask, gitTask, dotnetTask, nodeTask, npmTask, pythonTask);
        }
        catch
        {
            // Per-probe failures already return null; this fires only for framework errors.
            results = [await osTask, await gitTask, await dotnetTask, await nodeTask, await npmTask, await pythonTask];
        }

        string? os = results[0], gitV = results[1], dotnetV = results[2], nodeV = results[3], npmV = results[4], pythonV = results[5];

        string FirstLine(string? text) =>
            string.IsNullOrWhiteSpace(text) ? "not installed" : text.Trim().Split('\n')[0].Trim();

        var block = string.Join("\n",
            "[ENVIRONMENT]",
            $"OS: {FirstLine(os)}",
            "Shell: cmd.exe",
            $"git: {FirstLine(gitV)}",
            $"dotnet: {FirstLine(dotnetV)}",
            $"node: {FirstLine(nodeV)}",
            $"npm: {FirstLine(npmV)}",
            $"python: {FirstLine(pythonV)}",
            $"Workspace: {workspaceRoot}",
            "[/ENVIRONMENT]");

        var summary = $"git={Short(gitV)} dotnet={Short(dotnetV)} node={Short(nodeV)} python={Short(pythonV)}";
        return Outcome.Successful((block, summary));
    }

    private async Task<string?> RunAsync(string program, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(program, arguments, workingDirectory, timeoutSeconds: 3, cancellationToken);
            var output = (result.Output + " " + result.Error).Trim();
            return result.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string Short(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "none" : text.Trim().Split('\n')[0].Trim();
}
