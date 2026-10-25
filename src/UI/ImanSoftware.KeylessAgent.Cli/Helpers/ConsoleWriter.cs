using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli.Helpers;

/// <summary>
/// Consistent console output helpers. Plain ASCII markers render on every console,
/// including legacy code pages; all dynamic text is escaped.
/// </summary>
internal static class ConsoleWriter
{
    public static void Header(string workspace)
    {
        AnsiConsole.Write(new Rule("[cyan bold]KeylessAgent[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]Workspace:[/] [white]{workspace.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();
    }

    public static void Success(string message)
        => AnsiConsole.MarkupLine($"[green][[ok]][/] {message.EscapeMarkup()}");

    public static void Warning(string message)
        => AnsiConsole.MarkupLine($"[yellow][[!]][/] {message.EscapeMarkup()}");

    public static void Error(string message)
        => AnsiConsole.MarkupLine($"[red][[x]][/] {message.EscapeMarkup()}");

    public static void Info(string message)
        => AnsiConsole.MarkupLine($"[grey][[i]][/] {message.EscapeMarkup()}");
}
