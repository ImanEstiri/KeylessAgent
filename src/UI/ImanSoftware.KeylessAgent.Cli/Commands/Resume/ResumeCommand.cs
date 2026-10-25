using System.ComponentModel;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Resume;

internal sealed class ResumeSettings : CommandSettings
{
    [CommandArgument(0, "<request>")]
    [Description("The next request for the existing chat.")]
    public string Request { get; init; } = string.Empty;

    [CommandOption("-w|--workspace <PATH>")]
    [Description("Workspace directory (defaults to the current directory).")]
    public string? Workspace { get; init; }

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

internal sealed class ResumeCommand : AsyncCommand<ResumeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ResumeSettings settings, CancellationToken cancellationToken)
    {
        var workspace = settings.GetWorkspaceRoot();
        ConsoleWriter.Header(workspace);

        var sessionStore = KeylessAgentServiceFactory.CreateSessionStore();
        var session = await sessionStore.LoadAsync(workspace);
        if (session.IsFailure)
        {
            ConsoleWriter.Error("No saved session in .agent/session.json. Run 'keyless run \"<request>\"' first.");
            return 1;
        }

        ConsoleWriter.Success($"Resuming chat {session.Data.ChatUrl}");

        return await AgentCliRunner.RunTurnAsync(
            workspace, settings.Request.Trim(), dryRun: false, settings.Verbose, cancellationToken);
    }
}
