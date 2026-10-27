using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli.Helpers;

/// <summary>
/// A minimal IAgentUserInterface for commands that only need connector progress output
/// (no turn UI, no prompts).
/// </summary>
internal sealed class ConsoleUiAdapter : IAgentUserInterface
{
    public static ConsoleUiAdapter Instance { get; } = new();

    public void Say(string message)
        => AnsiConsole.MarkupLine($"[cyan]{message.EscapeMarkup()}[/]");

    public void ShowProgress(AgentProgressEvent progressEvent)
        => AnsiConsole.MarkupLine($"[grey][[i]][/] {progressEvent.Detail.EscapeMarkup()}");

    public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(true);

    public Task<string> AskAsync(string question) => Task.FromResult(string.Empty);

    public void ShowDiff(FileChange change)
    {
    }

    public IDisposable BeginWait(string message) => new NopScope();

    private sealed class NopScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
