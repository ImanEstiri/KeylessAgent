using System.ComponentModel;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Interactive;

internal sealed class InteractiveSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the git repository the agent will work on (prompted when omitted).")]
    public string? Workspace { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Write browser diagnostics to .agent/logs/diagnostics.log.")]
    public bool Verbose { get; init; }
}
