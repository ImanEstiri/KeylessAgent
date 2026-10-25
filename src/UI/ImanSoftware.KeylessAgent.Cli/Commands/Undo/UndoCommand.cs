using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Undo;

internal sealed class UndoSettings : CommandSettings
{
    [CommandOption("-w|--workspace <PATH>")]
    [Description("Workspace directory (defaults to the current directory).")]
    public string? Workspace { get; init; }

    public string GetWorkspaceRoot()
        => string.IsNullOrWhiteSpace(Workspace)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(Workspace);
}

internal sealed class UndoCommand : AsyncCommand<UndoSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UndoSettings settings, CancellationToken cancellationToken)
    {
        var workspace = settings.GetWorkspaceRoot();
        ConsoleWriter.Header(workspace);

        var backup = KeylessAgentServiceFactory.CreateBackupService();
        var outcome = await backup.UndoLatestAsync(workspace);

        if (outcome.IsFailure)
        {
            ConsoleWriter.Error(outcome.Error.Message);
            return 1;
        }

        ConsoleWriter.Success($"Restored {outcome.Data.Count} file(s) from the newest backup batch:");
        foreach (var file in outcome.Data)
            AnsiConsole.MarkupLine($"  [grey]•[/] {file.EscapeMarkup()}");

        return 0;
    }
}
