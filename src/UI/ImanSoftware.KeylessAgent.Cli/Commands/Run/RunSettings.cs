using System.ComponentModel;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Run;

internal sealed class RunSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the git repository the agent will work on (prompted when omitted).")]
    public string? Workspace { get; init; }

    [CommandArgument(1, "[request]")]
    [Description("What the agent should do, in one sentence (prompted when omitted).")]
    public string? Request { get; init; }

    [CommandOption("--dry-run")]
    [Description("Print what would happen and execute nothing.")]
    public bool DryRun { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Write browser diagnostics to .agent/logs/diagnostics.log.")]
    public bool Verbose { get; init; }

    [CommandOption("--timing")]
    [Description("Print the per-phase timing table after the turn.")]
    public bool Timing { get; init; }
}
