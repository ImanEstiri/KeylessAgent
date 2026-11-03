using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Show;

internal sealed class ShowSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the workspace whose keyless.json provides the browser settings (prompted when omitted).")]
    public string? Workspace { get; init; }

    [CommandOption("--peek <SECONDS>")]
    [Description("Show the window for this many seconds, then move it back offscreen (0 = stay visible).")]
    [DefaultValue(0)]
    public int Peek { get; init; }
}

/// <summary>
/// Brings the offscreen/minimized Chrome window into view on demand (pure CDP window move —
/// a running turn is never disturbed). <c>--peek N</c> returns it offscreen after N seconds.
/// </summary>
internal sealed class ShowCommand : AsyncCommand<ShowSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ShowSettings settings, CancellationToken cancellationToken)
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
        var outcome = await controller.ShowAsync(options.Data.Browser, Math.Max(0, settings.Peek), cancellationToken);
        if (outcome.IsFailure)
        {
            ConsoleWriter.Shared.Error(outcome.Error.Message);
            return 1;
        }

        ConsoleWriter.Shared.Success(settings.Peek > 0
            ? $"Chrome window shown. It will return offscreen in {settings.Peek} seconds."
            : $"Chrome window shown. Run 'keyless hide {workspace}' or close the window when done.");
        return 0;
    }
}
