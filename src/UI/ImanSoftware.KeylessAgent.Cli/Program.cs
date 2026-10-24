using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("keyless");
    config.SetApplicationVersion("1.0.0");
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
