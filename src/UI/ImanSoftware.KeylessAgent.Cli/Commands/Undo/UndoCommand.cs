using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Undo;

internal sealed class UndoSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the workspace (prompted when omitted).")]
    public string? Workspace { get; init; }
}

internal sealed class UndoCommand : AsyncCommand<UndoSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UndoSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        ConsoleWriter.Shared.Header(workspace);

        var backup = KeylessAgentServiceFactory.CreateBackupService();
        var outcome = await backup.UndoLatestAsync(workspace);

        if (outcome.IsFailure)
        {
            ConsoleWriter.Shared.Error(outcome.Error.Message);
            return 1;
        }

        ConsoleWriter.Shared.Success($"Restored {outcome.Data.Count} file(s) from the newest backup batch:");
        foreach (var file in outcome.Data)
            AnsiConsole.MarkupLine($"  [grey]•[/] {file.EscapeMarkup()}");

        return 0;
    }
}
