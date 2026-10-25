using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli.Helpers;

/// <summary>
/// Resolves the workspace path: the first input the app takes. A provided path is validated;
/// a missing one is prompted for (with the current directory only as the shown default,
/// never as a silent assumption).
/// </summary>
internal static class WorkspacePrompt
{
    /// <summary>
    /// Returns an existing workspace directory: <paramref name="provided"/> when valid,
    /// otherwise an interactive prompt that repeats until an existing directory is entered.
    /// </summary>
    /// <param name="provided">The workspace path argument (may be null or invalid).</param>
    /// <returns>The absolute path of an existing directory.</returns>
    public static string ResolveOrPrompt(string? provided)
    {
        if (!string.IsNullOrWhiteSpace(provided))
        {
            var full = Path.GetFullPath(provided.Trim());
            if (Directory.Exists(full))
                return full;

            AnsiConsole.MarkupLine($"[red]✗[/] Directory not found: {full.EscapeMarkup()}");
        }

        return AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]workspace>[/] Path of the git repository the agent will work on:")
                .DefaultValue(Directory.GetCurrentDirectory())
                .Validate(path =>
                {
                    var full = Path.GetFullPath(path);
                    return Directory.Exists(full)
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Directory not found: {full}");
                }));
    }
}
