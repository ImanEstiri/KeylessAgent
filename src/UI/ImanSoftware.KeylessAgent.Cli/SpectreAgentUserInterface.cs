using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli;

/// <summary>
/// The Spectre.Console implementation of the UI contract: say messages as panels,
/// progress through the shared status spinner, questions and confirmations as prompts,
/// and colored diffs before file changes.
/// </summary>
internal sealed class SpectreAgentUserInterface : IAgentUserInterface
{
    private StatusContext? _status;

    /// <summary>
    /// Attaches the status context the command started, so progress updates land in the spinner line.
    /// </summary>
    public void AttachStatus(StatusContext status) => _status = status;

    /// <inheritdoc />
    public void Say(string message)
    {
        var panel = new Panel(message.EscapeMarkup())
            .Header("[cyan]DeepSeek says[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Cyan1);
        AnsiConsole.Write(panel);
    }

    /// <inheritdoc />
    public void ShowProgress(AgentProgressEvent progressEvent)
    {
        var text = $"[grey]{progressEvent.Type}[/] {progressEvent.Detail.EscapeMarkup()}";
        if (_status is not null)
            _status.Status(text);
        else
            AnsiConsole.MarkupLine(text);
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        var prompt = new ConfirmationPrompt($"[yellow]?[/] {request.Title.EscapeMarkup()}: {request.Details.EscapeMarkup()}");
        var result = prompt.Show(AnsiConsole.Console);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<string> AskAsync(string question)
    {
        var answer = AnsiConsole.Prompt(new TextPrompt<string>($"[yellow]?[/] {question.EscapeMarkup()}"));
        return Task.FromResult(answer);
    }

    /// <inheritdoc />
    public void ShowDiff(FileChange change)
    {
        var title = change.Kind switch
        {
            FileChangeKind.Create => $"[green]+ new file: {change.Path}[/]",
            FileChangeKind.Update => $"[yellow]~ update file: {change.Path}[/]",
            FileChangeKind.Edit => $"[yellow]~ edit file: {change.Path}[/]",
            _ => $"[red]- delete file: {change.Path}[/]",
        };

        var grid = new Grid().Expand();
        grid.AddColumn();
        grid.AddRow(title);

        if (change.OldContent is not null || change.NewContent is not null)
        {
            var oldLines = (change.OldContent ?? string.Empty).Split('\n');
            var newLines = (change.NewContent ?? string.Empty).Split('\n');
            var max = Math.Max(oldLines.Length, newLines.Length);
            for (var i = 0; i < max && i < 40; i++)
            {
                var oldLine = i < oldLines.Length ? oldLines[i] : null;
                var newLine = i < newLines.Length ? newLines[i] : null;
                if (oldLine == newLine)
                    continue;

                if (oldLine is not null)
                    grid.AddRow($"[red]- {oldLine.EscapeMarkup()}[/]");
                if (newLine is not null)
                    grid.AddRow($"[green]+ {newLine.EscapeMarkup()}[/]");
            }

            if (max > 40)
                grid.AddRow($"[grey]… {max - 40} more lines[/]");
        }

        AnsiConsole.Write(grid);
    }
}
