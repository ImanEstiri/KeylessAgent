using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Selectors;

internal sealed class SelectorsRefreshSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the workspace whose selectors.json is refreshed (prompted when omitted).")]
    public string? Workspace { get; init; }
}

/// <summary>
/// Forces the selectors.json merge UNCONDITIONALLY (ignoring the CatalogVersion check):
/// never-customized keys are refreshed to the current embedded defaults, genuine user
/// customizations survive, and the original file is backed up as selectors.json.v&lt;N&gt;.bak.
/// For picking up new selectors immediately instead of waiting for the next load.
/// </summary>
internal sealed class SelectorsRefreshCommand : AsyncCommand<SelectorsRefreshSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SelectorsRefreshSettings settings, CancellationToken cancellationToken)
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

        var loggerFactory = KeylessAgentServiceFactory.CreateFileLoggerFactory(workspace);
        var merge = await SelectorsCatalog.MergeAsync(
            workspace, options.Data.Browser.SelectorsFile, forceRefresh: true, loggerFactory, cancellationToken);
        if (merge.IsFailure)
        {
            ConsoleWriter.Shared.Error(merge.Error.Message);
            return 1;
        }

        var refreshed = merge.Data.RefreshedKeys;
        var writer = ConsoleWriter.Shared;
        if (refreshed.Count == 0)
        {
            writer.Success("selectors.json refreshed: no keys needed an update — everything is current or user-customized.");
        }
        else
        {
            writer.Success($"selectors.json refreshed: {refreshed.Count} key(s) updated to the current embedded defaults:");
            foreach (var key in refreshed)
                writer.Info($"  • {key}");
        }

        writer.Info("A backup of the previous file was written next to it (selectors.json.v<N>.bak).");
        return 0;
    }
}
