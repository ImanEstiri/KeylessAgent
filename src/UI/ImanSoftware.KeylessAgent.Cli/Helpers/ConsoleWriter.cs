using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli.Helpers;

/// <summary>
/// Consistent console output helpers. Plain ASCII markers render on every console,
/// including legacy code pages; all dynamic text is escaped. During a turn, all output
/// goes through the same IAnsiConsole as the agent UI (never a second live surface).
/// </summary>
internal sealed class ConsoleWriter(IAnsiConsole console)
{
    /// <summary>The process-wide writer bound to the active console.</summary>
    public static ConsoleWriter Shared { get; } = new(AnsiConsole.Console);

    public void Header(string workspace)
    {
        console.Write(new Rule("[cyan bold]KeylessAgent[/]").RuleStyle("grey").LeftJustified());
        console.MarkupLine($"[grey]Workspace:[/] [white]{workspace.EscapeMarkup()}[/]");
        console.WriteLine();
    }

    public void Success(string message)
        => console.MarkupLine($"[green][[ok]][/] {message.EscapeMarkup()}");

    public void Warning(string message)
        => console.MarkupLine($"[yellow][[!]][/] {message.EscapeMarkup()}");

    public void Error(string message)
        => console.MarkupLine($"[red][[x]][/] {message.EscapeMarkup()}");

    public void Info(string message)
        => console.MarkupLine($"[grey][[i]][/] {message.EscapeMarkup()}");
}
