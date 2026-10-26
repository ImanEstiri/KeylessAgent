using System.ComponentModel;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Doctor;

internal sealed class DoctorSettings : CommandSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Path of the workspace whose selectors.json is checked (prompted when omitted).")]
    public string? Workspace { get; init; }

    [CommandOption("--capture <DIR>")]
    [Description("Save trimmed HTML snapshots of the composer, newest message, sidebar and chips into this directory.")]
    public string? Capture { get; init; }

    [CommandOption("--watch")]
    [Description("Wait while you type a prompt and send it manually, then capture the generating and finished states.")]
    public bool Watch { get; init; }
}

/// <summary>
/// Live selector check: attaches to Chrome, opens the chat page in a new tab and reports
/// every selector's state without sending anything or changing the chat.
/// Exit code 0 only when all required selectors for the current state matched.
/// </summary>
internal sealed class DoctorCommand : AsyncCommand<DoctorSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings, CancellationToken cancellationToken)
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

        var diagnostics = KeylessAgentServiceFactory.CreateChatDiagnostics();
        var reportOutcome = await diagnostics.InspectAsync(
            workspace, options.Data.Browser, settings.Capture, settings.Watch, cancellationToken);

        if (reportOutcome.IsFailure)
        {
            ConsoleWriter.Shared.Error(reportOutcome.Error.Message);
            return 1;
        }

        return Print(reportOutcome.Data);
    }

    private static int Print(ChatDiagnosticsReport report)
    {
        var writer = ConsoleWriter.Shared;
        writer.Info($"Page: {report.PageUrl}");
        writer.Info(report.LoggedIn
            ? "Logged in: yes"
            : "Logged in: NO — the login page is shown. Log in manually and re-run doctor.");
        writer.Info($"DeepThink aria-pressed: {report.DeepThinkPressed ?? "not found"} | Search aria-pressed: {report.SearchPressed ?? "not found"}");
        writer.Info($"Input accepts text: {(report.InputAcceptsText ? "yes" : "NO")} | All required selectors matched: {(report.AllRequiredMatched ? "yes" : "NO")}");

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[grey]Selector[/]");
        table.AddColumn("[grey]State[/]");
        table.AddColumn("[grey]Fallback[/]");
        table.AddColumn("[grey]Elements[/]");
        table.AddColumn("[grey]Detail[/]");

        foreach (var check in report.Checks)
        {
            var state = check.Status switch
            {
                SelectorCheckStatus.Matched => "[green]MATCHED[/]",
                SelectorCheckStatus.NotFound => "[red]NOT FOUND[/]",
                _ => "[grey]NOT APPLICABLE NOW[/]",
            };

            table.AddRow(
                check.Key,
                state,
                check.Status == SelectorCheckStatus.Matched ? $"#{check.FallbackIndex}" : "-",
                check.Status == SelectorCheckStatus.Matched ? check.MatchCount.ToString() : "-",
                check.Detail.EscapeMarkup());
        }

        AnsiConsole.Write(table);

        if (report.CapturedFiles.Count > 0)
        {
            writer.Info("Captured snapshots:");
            foreach (var file in report.CapturedFiles)
                AnsiConsole.MarkupLine($"  [grey]•[/] {file.EscapeMarkup()}");
        }

        if (!report.AllRequiredMatched)
        {
            writer.Warning("Fix the NOT FOUND selectors in selectors.json (see docs/browser-automation.md) and re-run.");
            return 1;
        }

        writer.Success("All required selectors matched.");
        return 0;
    }
}
