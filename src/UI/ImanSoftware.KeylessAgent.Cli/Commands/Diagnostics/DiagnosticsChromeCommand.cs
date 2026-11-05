using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Diagnostics;

internal sealed class DiagnosticsChromeSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the workspace (prompted when omitted; used for the header only).")]
    public string? Workspace { get; init; }
}

/// <summary>
/// Prints the RUNNING Chrome's full command line (Get-CimInstance Win32_Process) so the user
/// can verify the anti-occlusion flags are present, then a verdict for the two flags that
/// matter: --disable-features=CalculateNativeWinOcclusion and --window-position.
/// </summary>
internal sealed class DiagnosticsChromeCommand : AsyncCommand<DiagnosticsChromeSettings>
{
    private static readonly Regex ProcessIdLine = new(@"ProcessId\s*:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex CommandLineLine = new(@"CommandLine\s*:\s*(.+)$", RegexOptions.Compiled);

    public override async Task<int> ExecuteAsync(CommandContext context, DiagnosticsChromeSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        ConsoleWriter.Shared.Header(workspace);

        if (!OperatingSystem.IsWindows())
        {
            ConsoleWriter.Shared.Error("This diagnostic is Windows-only (Get-CimInstance Win32_Process).");
            return 1;
        }

        var writer = ConsoleWriter.Shared;
        var processes = await ReadChromeProcessesAsync(cancellationToken);
        if (processes.Count == 0)
        {
            writer.Warning("No chrome.exe processes found. Run 'keyless chrome <workspace>' first, then re-run this command.");
            return 1;
        }

        writer.Info($"chrome.exe processes found: {processes.Count}. Full command lines:");
        foreach (var (pid, commandLine) in processes)
            writer.Info($"  PID {pid}: {commandLine ?? "(command line unavailable)"}");

        // The two lines that matter for the offscreen-throttling diagnosis.
        var withFeatures = processes.Where(p => p.CommandLine?.Contains("disable-features", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var withPosition = processes.Where(p => p.CommandLine?.Contains("window-position", StringComparison.OrdinalIgnoreCase) == true).ToList();

        writer.Info("Lines containing 'disable-features':");
        foreach (var (_, commandLine) in withFeatures)
            writer.Info($"  {commandLine}");
        if (withFeatures.Count == 0)
            writer.Warning("  (none)");

        writer.Info("Lines containing 'window-position':");
        foreach (var (_, commandLine) in withPosition)
            writer.Info($"  {commandLine}");
        if (withPosition.Count == 0)
            writer.Warning("  (none)");

        var occlusionDisabled = withFeatures.Any(p =>
            p.CommandLine!.Contains("CalculateNativeWinOcclusion", StringComparison.OrdinalIgnoreCase));

        if (occlusionDisabled)
            writer.Success("Verdict: --disable-features contains CalculateNativeWinOcclusion — the occlusion feature is disabled for this Chrome.");
        else
            writer.Warning("Verdict: CalculateNativeWinOcclusion is NOT in --disable-features. This Chrome was NOT launched by KeylessAgent (or an older launch) — close it and run 'keyless chrome' so the anti-throttling flags apply.");

        if (withPosition.Count == 0)
            writer.Warning("Verdict: no --window-position flag — the window position comes from CDP moves (this is fine when Chrome was attached rather than launched).");
        return 0;
    }

    /// <summary>
    /// Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Select ProcessId, CommandLine | Format-List,
    /// parsed into (pid, command line) pairs.
    /// </summary>
    private static async Task<List<(int Pid, string? CommandLine)>> ReadChromeProcessesAsync(CancellationToken cancellationToken)
    {
        var results = new List<(int Pid, string? CommandLine)>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='chrome.exe'\\\" | Select-Object ProcessId, CommandLine | Format-List\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return results;

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            int? currentPid = null;
            foreach (var line in output.Split('\n'))
            {
                var pidMatch = ProcessIdLine.Match(line);
                if (pidMatch.Success)
                {
                    currentPid = int.Parse(pidMatch.Groups[1].Value);
                    continue;
                }

                var commandMatch = CommandLineLine.Match(line.TrimEnd('\r', '\n'));
                if (commandMatch.Success && currentPid is not null)
                {
                    results.Add((currentPid.Value, commandMatch.Groups[1].Value.Trim()));
                    currentPid = null;
                }
            }
        }
        catch
        {
            // PowerShell missing or denied: the caller reports an empty list gracefully.
        }

        return results;
    }
}
