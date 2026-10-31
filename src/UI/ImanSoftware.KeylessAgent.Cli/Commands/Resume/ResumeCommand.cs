using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Resume;

internal sealed class ResumeSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the git repository the agent will work on (prompted when omitted).")]
    public string? Workspace { get; init; }

    [CommandArgument(1, "[request]")]
    [Description("The next request for the existing chat (prompted when omitted).")]
    public string? Request { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Write browser diagnostics to .agent/logs/diagnostics.log.")]
    public bool Verbose { get; init; }
}

internal sealed class ResumeCommand : AsyncCommand<ResumeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ResumeSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        var request = string.IsNullOrWhiteSpace(settings.Request)
            ? AnsiConsole.Prompt(new TextPrompt<string>("[cyan]request>[/] The next request for the existing chat:"))
            : settings.Request.Trim();

        ConsoleWriter.Shared.Header(workspace);

        var sessionStore = KeylessAgentServiceFactory.CreateSessionStore();
        var session = await sessionStore.LoadAsync(workspace);
        if (session.IsFailure)
        {
            ConsoleWriter.Shared.Error("No saved session in .agent/session.json. Run 'keyless run <workspace> <request>' first.");
            return 1;
        }

        ConsoleWriter.Shared.Success($"Resuming chat {session.Data.ChatUrl}");

        return await AgentCliRunner.RunTurnAsync(
            workspace, request, dryRun: false, settings.Verbose, cancellationToken);
    }
}
