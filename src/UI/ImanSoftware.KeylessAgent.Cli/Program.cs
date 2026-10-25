using ImanSoftware.KeylessAgent.Cli.Commands.Config;
using ImanSoftware.KeylessAgent.Cli.Commands.Interactive;
using ImanSoftware.KeylessAgent.Cli.Commands.Resume;
using ImanSoftware.KeylessAgent.Cli.Commands.Run;
using ImanSoftware.KeylessAgent.Cli.Commands.Undo;
using Spectre.Console;
using Spectre.Console.Cli;

// UTF-8 in and out so panels, diffs and file content render; no-ops when redirected
// or on consoles that refuse the switch.
if (!Console.IsOutputRedirected)
{
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* legacy console */ }
}

if (!Console.IsInputRedirected)
{
    try { Console.InputEncoding = System.Text.Encoding.UTF8; } catch { /* legacy console */ }
}

var app = new CommandApp<InteractiveCommand>();

app.Configure(config =>
{
    config.SetApplicationName("keyless");
    config.SetApplicationVersion("1.0.0");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Run one agent turn on a workspace. The workspace path is the first argument (or the first thing you are asked for).")
        .WithExample("run", @"G:\Projects\MyApp", "Add a README section about running tests")
        .WithExample("run", @"G:\Projects\MyApp", "Fix the failing parser test", "--dry-run")
        .WithExample("run", @"G:\Projects\MyApp", "Refactor the config loader", "-v")
        .WithExample("run");

    config.AddCommand<ResumeCommand>("resume")
        .WithDescription("Send the next request to the chat saved in <workspace>/.agent/session.json.")
        .WithExample("resume", @"G:\Projects\MyApp", "Continue with the tests now");

    config.AddCommand<UndoCommand>("undo")
        .WithDescription("Restore the newest backup batch from <workspace>/.agent/backups.")
        .WithExample("undo", @"G:\Projects\MyApp");

    config.AddCommand<ConfigCommand>("config")
        .WithDescription("Show the effective configuration of a workspace (keyless.json is created with defaults when missing).")
        .WithExample("config", @"G:\Projects\MyApp");
});

try
{
    return await app.RunAsync(args);
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    return 1;
}
