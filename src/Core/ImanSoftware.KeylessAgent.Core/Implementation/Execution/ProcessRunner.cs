using System.ComponentModel;
using System.Diagnostics;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// The result of one external process run.
/// </summary>
/// <param name="ExitCode">The process exit code; -1 when the process was killed on timeout.</param>
/// <param name="Output">Captured standard output.</param>
/// <param name="Error">Captured standard error.</param>
/// <param name="DurationSeconds">Wall-clock duration in seconds.</param>
/// <param name="TimedOut">Whether the process exceeded its timeout and was killed.</param>
internal sealed record ProcessResult(int ExitCode, string Output, string Error, double DurationSeconds, bool TimedOut)
{
    /// <summary>
    /// Whether the process never started (executable not found or not accessible).
    /// </summary>
    public bool NotStarted { get; init; }
}

/// <summary>
/// Runs external processes (git, dotnet, the snapshot bat files) with output capture,
/// a hard timeout and full process-tree kill. Process execution deliberately does not go through IFileStorage.
/// </summary>
internal sealed class ProcessRunner
{
    /// <summary>
    /// Runs a process to completion without a console window.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">The argument string.</param>
    /// <param name="workingDirectory">The working directory for the process.</param>
    /// <param name="timeoutSeconds">Wall-clock timeout; the process tree is killed when exceeded.</param>
    /// <param name="cancellationToken">Additional cancellation; kills the process tree when triggered.</param>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            // The executable does not exist or cannot be accessed; reported as command_not_found.
            return new ProcessResult(-1, string.Empty, string.Empty, 0, TimedOut: false) { NotStarted = true };
        }
        // Close stdin immediately so batch files that end with `pause` can never hang waiting for a key.
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            var duration = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
            return new ProcessResult(-1, string.Empty, string.Empty, duration, TimedOut: !cancellationToken.IsCancellationRequested);
        }

        var output = await outputTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        var error = await errorTask.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        var totalDuration = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;

        return new ProcessResult(process.ExitCode, output, error, totalDuration, TimedOut: false);
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between the check and the kill; nothing to do.
        }
    }
}
