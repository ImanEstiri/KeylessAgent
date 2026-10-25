using System.ComponentModel;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Config;

internal sealed class ConfigSettings : CommandSettings
{
    [CommandOption("-w|--workspace <PATH>")]
    [Description("Workspace directory (defaults to the current directory).")]
    public string? Workspace { get; init; }

    public string GetWorkspaceRoot()
        => string.IsNullOrWhiteSpace(Workspace)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(Workspace);
}

internal sealed class ConfigCommand : AsyncCommand<ConfigSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigSettings settings, CancellationToken cancellationToken)
    {
        var workspace = settings.GetWorkspaceRoot();
        ConsoleWriter.Header(workspace);

        var configuration = KeylessAgentServiceFactory.CreateConfigurationService();
        var outcome = await configuration.LoadAsync(workspace);

        if (outcome.IsFailure)
        {
            ConsoleWriter.Error(outcome.Error.Message);
            return 1;
        }

        var options = outcome.Data;
        ConsoleWriter.Success($"Effective configuration (keyless.json, created with defaults when missing):");

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[grey]Section[/]");
        table.AddColumn("[grey]Values[/]");

        table.AddRow("Mode", options.Mode.ToString());
        table.AddRow("Browser", $"port={options.Browser.ChromeDebugPort}, DeepThink={options.Browser.EnableDeepThink}, Search={options.Browser.EnableSearch}, selectors={options.Browser.SelectorsFile}, replyTimeout={options.Browser.ReplyTimeoutSeconds}s, rate={options.Browser.MaxMessagesPerMinute}/min, debug={options.Browser.Debug}");
        table.AddRow("Limits", $"commands/response={options.Limits.MaxCommandsPerResponse}, responseChars={options.Limits.MaxResponseChars}, fileChars={options.Limits.MaxFileContentChars}, outputChars={options.Limits.MaxCommandOutputChars}, formatRetries={options.Limits.MaxFormatRetries}, iterations={options.Limits.MaxIterationsPerTurn}");
        table.AddRow("Snapshot", $"mode={options.Snapshot.Mode}, attachOnFeedback={options.Snapshot.AttachOnFeedback}, refreshAfterEachBatch={options.Snapshot.RefreshAfterEachBatch}");
        table.AddRow("Git", $"prefix={options.Git.BranchPrefix}, protected=[{string.Join(", ", options.Git.ProtectedBranchPatterns)}]");
        table.AddRow("Commands", $"allowlist=[{string.Join(", ", options.Commands.Allowlist)}], timeout={options.Commands.TimeoutSeconds}s");

        AnsiConsole.Write(table);
        return 0;
    }
}
