using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Agent;

/// <summary>
/// A failing log write (unwritable path, I/O error) must disable logging for the rest of the
/// turn with a single warning — never throw, never fail the turn.
/// </summary>
public class TurnLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-tlog-" + Guid.CreateVersion7().ToString("N"));

    public TurnLoggerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task WriteFailure_DisablesLoggingWithoutThrowing()
    {
        // The log path is blocked: a FILE exists where the logs directory should be.
        var logsPath = Path.Combine(_root, ".agent", "logs");
        Directory.CreateDirectory(Path.GetDirectoryName(logsPath)!);
        await File.WriteAllTextAsync(logsPath, "a file blocks the directory");

        var warnings = new List<string>();
        var logger = new TurnLogger(
            ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider(), warnings.Add);

        var start = async () => await logger.StartAsync(_root, 1);
        var append = async () => await logger.AppendAsync("note", new { X = 1 });
        var appendAgain = async () => await logger.AppendAsync("note", new { X = 2 });

        await start.Should().NotThrowAsync();
        await append.Should().NotThrowAsync();
        await appendAgain.Should().NotThrowAsync();

        warnings.Should().ContainSingle(w => w.Contains("Turn log disabled"),
            "exactly one warning, no matter how many writes follow the first failure");
        warnings.Count.Should().Be(1);
    }

    [Fact]
    public async Task HealthyLog_WritesWithoutWarnings()
    {
        var warnings = new List<string>();
        var logger = new TurnLogger(
            ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider(), warnings.Add);

        await logger.StartAsync(_root, 1);
        await logger.AppendAsync("note", new { X = 1 });

        warnings.Should().BeEmpty();
        File.Exists(logger.LogPath).Should().BeTrue();
        (await File.ReadAllTextAsync(logger.LogPath)).Should().Contain("\"note\"");
    }

    [Fact]
    public async Task ResultOutput_SecretsAreRedacted()
    {
        var logger = new TurnLogger(
            ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider());
        await logger.StartAsync(_root, 1);

        await logger.AppendResultAsync(new CommandExecutionResult
        {
            Number = 1,
            Kind = CommandKind.RunCommand,
            Target = "print-credentials",
            Status = CommandStatus.Ok,
            Output = "password=secret123\nDB_HOST=localhost",
        });

        var log = await File.ReadAllTextAsync(logger.LogPath);
        log.Should().NotContain("secret123", "credentials never reach the turn log");
        log.Should().Contain("[redacted]");
        log.Should().Contain("password=[redacted]");
        log.Should().Contain("DB_HOST=localhost", "non-secret lines are untouched");
    }

    [Fact]
    public async Task ResultOutput_JsonAndBearerSecrets_AreRedacted()
    {
        var logger = new TurnLogger(
            ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider());
        await logger.StartAsync(_root, 1);

        await logger.AppendResultAsync(new CommandExecutionResult
        {
            Number = 1,
            Kind = CommandKind.RunCommand,
            Target = "print-credentials",
            Status = CommandStatus.Ok,
            Output = "\"ApiKey\": \"sk-123\"\nAuthorization: Bearer eyJhbGci\nREGION=eu",
        });

        var log = await File.ReadAllTextAsync(logger.LogPath);
        log.Should().NotContain("sk-123");
        log.Should().NotContain("eyJhbGci");
        // The serializer escapes quotes as unicode escapes, so the redacted values are counted instead of
        // asserted verbatim: one for the JSON key, one for the Bearer token.
        log.Split("[redacted]").Length.Should().Be(3);
        log.Should().Contain("Authorization: Bearer [redacted]", "the token is gone, the scheme is kept");
        log.Should().Contain("REGION=eu");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }
}
