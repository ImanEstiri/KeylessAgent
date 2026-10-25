using System.ComponentModel;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Interactive;

internal sealed class InteractiveSettings : CommandSettings
{
    [CommandOption("-w|--workspace <PATH>")]
    [Description("Workspace directory (defaults to the current directory).")]
    public string? Workspace { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Write browser diagnostics to .agent/logs/diagnostics.log.")]
    public bool Verbose { get; init; }

    public string GetWorkspaceRoot()
        => string.IsNullOrWhiteSpace(Workspace)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(Workspace);
}
