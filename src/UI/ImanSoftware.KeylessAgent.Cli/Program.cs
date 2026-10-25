using ImanSoftware.KeylessAgent.Cli.Commands.Config;
using ImanSoftware.KeylessAgent.Cli.Commands.Interactive;
using ImanSoftware.KeylessAgent.Cli.Commands.Resume;
using ImanSoftware.KeylessAgent.Cli.Commands.Run;
using ImanSoftware.KeylessAgent.Cli.Commands.Undo;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<InteractiveCommand>();

app.Configure(config =>
{
    config.SetApplicationName("keyless");
    config.SetApplicationVersion("1.0.0");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Run one agent turn for a request in the current workspace.")
        .WithExample("run", "Add a README section about running tests")
        .WithExample("run", "Fix the failing parser test", "--dry-run")
        .WithExample("run", "Refactor the config loader", "-v");

    config.AddCommand<ResumeCommand>("resume")
        .WithDescription("Send the next request to the chat saved in .agent/session.json.")
        .WithExample("resume", "Continue with the tests now");

    config.AddCommand<UndoCommand>("undo")
        .WithDescription("Restore the newest backup batch from .agent/backups.")
        .WithExample("undo");

    config.AddCommand<ConfigCommand>("config")
        .WithDescription("Show the effective configuration (keyless.json is created with defaults when missing).")
        .WithExample("config");
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
