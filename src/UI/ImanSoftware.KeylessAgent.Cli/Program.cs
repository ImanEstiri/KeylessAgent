using ImanSoftware.KeylessAgent.Cli.Commands.Chrome;
using ImanSoftware.KeylessAgent.Cli.Commands.Config;
using ImanSoftware.KeylessAgent.Cli.Commands.Doctor;
using ImanSoftware.KeylessAgent.Cli.Commands.Hide;
using ImanSoftware.KeylessAgent.Cli.Commands.Show;
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
    config.SetApplicationVersion(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

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

    config.AddCommand<ChromeCommand>("chrome")
        .WithDescription("Start the dedicated Chrome with the KeylessAgent profile so you can log in to DeepSeek once.")
        .WithExample("chrome", @"G:\Projects\MyApp");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Live selector check: attaches to Chrome and reports every selector's state without sending anything.")
        .WithExample("doctor", @"G:\Projects\MyApp")
        .WithExample("doctor", @"G:\Projects\MyApp", "--capture", @".\doctor-capture")
        .WithExample("doctor", @"G:\Projects\MyApp", "--watch");

    config.AddCommand<ShowCommand>("show")
        .WithDescription("Bring the offscreen/minimized Chrome window into view (pure CDP window move; never disturbs a running turn).")
        .WithExample("show", @"G:\Projects\MyApp")
        .WithExample("show", @"G:\Projects\MyApp", "--peek", "3");

    config.AddCommand<HideCommand>("hide")
        .WithDescription("Move the Chrome window back offscreen after a manual show.")
        .WithExample("hide", @"G:\Projects\MyApp");

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
