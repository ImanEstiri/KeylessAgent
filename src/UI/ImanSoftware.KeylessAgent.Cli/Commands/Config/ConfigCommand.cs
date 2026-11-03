using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Config;

internal sealed class ConfigSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the workspace (prompted when omitted).")]
    public string? Workspace { get; init; }
}

internal sealed class ConfigCommand : AsyncCommand<ConfigSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        ConsoleWriter.Shared.Header(workspace);

        var configuration = KeylessAgentServiceFactory.CreateConfigurationService();
        var outcome = await configuration.LoadAsync(workspace);

        if (outcome.IsFailure)
        {
            ConsoleWriter.Shared.Error(outcome.Error.Message);
            return 1;
        }

        var options = outcome.Data;
        ConsoleWriter.Shared.Success("Effective configuration (keyless.json, created with defaults when missing):");

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[grey]Section[/]");
        table.AddColumn("[grey]Values[/]");

        table.AddRow("Mode", options.Mode.ToString());
        table.AddRow("Browser", $"port={options.Browser.ChromeDebugPort}, windowMode={options.Browser.WindowMode}, minimizeAfterLogin={options.Browser.MinimizeAfterLogin}, DeepThink={options.Browser.EnableDeepThink}, Search={options.Browser.EnableSearch}, selectors={options.Browser.SelectorsFile}, replyTimeout={options.Browser.ReplyTimeoutSeconds}s, rate={options.Browser.MaxMessagesPerMinute}/min, debug={options.Browser.Debug}");
        table.AddRow("Limits", $"commands/response={options.Limits.MaxCommandsPerResponse}, responseChars={options.Limits.MaxResponseChars}, fileChars={options.Limits.MaxFileContentChars}, outputChars={options.Limits.MaxCommandOutputChars}, formatRetries={options.Limits.MaxFormatRetries}, iterations={options.Limits.MaxIterationsPerTurn}");
        table.AddRow("Snapshot", $"mode={options.Snapshot.Mode}, attachOnFeedback={options.Snapshot.AttachOnFeedback}, refreshAfterEachBatch={options.Snapshot.RefreshAfterEachBatch}");
        // Table cells render as Spectre markup: the bracket-joined lists MUST be escaped,
        // or "[main, master, release*]" parses as a (malformed) markup tag.
        table.AddRow("Git", $"prefix={options.Git.BranchPrefix}, protected=[{string.Join(", ", options.Git.ProtectedBranchPatterns)}]".EscapeMarkup());
        table.AddRow("Commands", $"allowlist=[{string.Join(", ", options.Commands.Allowlist)}], timeout={options.Commands.TimeoutSeconds}s".EscapeMarkup());

        AnsiConsole.Write(table);
        return 0;
    }
}
