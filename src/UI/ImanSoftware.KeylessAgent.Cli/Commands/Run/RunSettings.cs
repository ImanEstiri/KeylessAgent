using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Run;

internal sealed class RunSettings : CommandSettings
{
    [CommandArgument(0, "<request>")]
    [Description("What the agent should do, in one sentence.")]
    public string Request { get; init; } = string.Empty;

    [CommandOption("-w|--workspace <PATH>")]
    [Description("Workspace directory (defaults to the current directory).")]
    public string? Workspace { get; init; }

    [CommandOption("--dry-run")]
    [Description("Print what would happen and execute nothing.")]
    public bool DryRun { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Write browser diagnostics to .agent/logs/diagnostics.log.")]
    public bool Verbose { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(Request))
            return ValidationResult.Error("The request cannot be empty.");

        return ValidationResult.Success();
    }

    public string GetWorkspaceRoot()
        => string.IsNullOrWhiteSpace(Workspace)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(Workspace);
}
