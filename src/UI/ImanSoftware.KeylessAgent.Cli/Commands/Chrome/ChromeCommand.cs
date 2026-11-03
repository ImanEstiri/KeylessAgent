using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Chrome;

internal sealed class ChromeSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the workspace whose keyless.json provides the browser settings (prompted when omitted).")]
    public string? Workspace { get; init; }
}

/// <summary>
/// Starts the dedicated Chrome with the KeylessAgent profile and the debug port — for users
/// who want to log in to DeepSeek once before running any turn. Never attaches or automates.
/// </summary>
internal sealed class ChromeCommand : AsyncCommand<ChromeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ChromeSettings settings, CancellationToken cancellationToken)
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

        var browser = options.Data.Browser;
        var connector = KeylessAgentServiceFactory.CreateBrowserConnector();

        // The connector's probe/launch path does exactly what this command needs: it starts
        // Chrome when the port is closed and reports each decision. It also attaches so the
        // user sees the session works; no message is ever sent.
        var connection = await connector.ConnectAsync(browser, ConsoleUiAdapter.Instance, cancellationToken);
        if (connection.IsFailure)
        {
            ConsoleWriter.Shared.Error(connection.Error.Message);
            return 1;
        }

        var kind = connection.Data.Attachment.Kind;

        // A hidden window (minimized/offscreen) would make the login page invisible: bring it
        // into view BEFORE the ready message. Pure CDP window move — no automation, no navigation.
        var windowOutcome = await KeylessAgentServiceFactory.CreateChromeWindowController(
            KeylessAgentServiceFactory.CreateFileLoggerFactory(workspace)).ShowAsync(browser, 0, cancellationToken);
        if (windowOutcome.IsFailure)
            ConsoleWriter.Shared.Warning($"The Chrome window could not be shown: {windowOutcome.Error.Message}");

        ConsoleWriter.Shared.Success($"Chrome is ready ({kind}). Log in to DeepSeek in that window once — the login persists in the dedicated profile.");
        if (windowOutcome.IsSuccess)
            ConsoleWriter.Shared.Info("Chrome window shown. 'keyless hide <workspace>' moves it offscreen again.");
        ConsoleWriter.Shared.Info($"Profile: {connection.Data.Attachment.UserDataDir}");
        ConsoleWriter.Shared.Info("You can now run 'keyless run <workspace> <request>'. This command does not automate the page.");
        return 0;
    }
}
