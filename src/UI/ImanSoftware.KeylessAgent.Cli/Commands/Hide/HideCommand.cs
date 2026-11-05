using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Hide;

internal sealed class HideSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the workspace whose keyless.json provides the browser settings (prompted when omitted).")]
    public string? Workspace { get; init; }
}

/// <summary>
/// Moves the Chrome window back offscreen (-32000,-32000) after a manual
/// <c>keyless show</c> — pure CDP window move, never disturbs a running turn.
/// </summary>
internal sealed class HideCommand : AsyncCommand<HideSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, HideSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        ConsoleWriter.Shared.Header(workspace);

        var configuration = KeylessAgentServiceFactory.CreateConfigurationService();
        var options = await configuration.LoadAsync(workspace);
        if (options.IsFailure)
        {
            ConsoleWriter.Shared.Error(options.Error.Message);
            return 1;
        }

        var controller = KeylessAgentServiceFactory.CreateChromeWindowController(
            KeylessAgentServiceFactory.CreateFileLoggerFactory(workspace));
        var outcome = await controller.HideAsync(options.Data.Browser, cancellationToken);
        if (outcome.IsFailure)
        {
            ConsoleWriter.Shared.Error(outcome.Error.Message);
            return 1;
        }

        ConsoleWriter.Shared.Success("Chrome window moved offscreen.");
        return 0;
    }
}
