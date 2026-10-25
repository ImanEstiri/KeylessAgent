using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Run;

internal sealed class RunCommand : AsyncCommand<RunSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings, CancellationToken cancellationToken)
    {
        var workspace = settings.GetWorkspaceRoot();
        ConsoleWriter.Header(workspace);

        return await AgentCliRunner.RunTurnAsync(
            workspace, settings.Request.Trim(), settings.DryRun, settings.Verbose, cancellationToken);
    }
}
