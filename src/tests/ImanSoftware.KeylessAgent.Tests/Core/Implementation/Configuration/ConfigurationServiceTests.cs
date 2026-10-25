using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Configuration;

public class ConfigurationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-config-" + Guid.CreateVersion7().ToString("N"));

    public ConfigurationServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    private ConfigurationService Service() => new();

    [Fact]
    public async Task LoadAsync_MissingFile_WritesDefaultAndReturnsIt()
    {
        var outcome = await Service().LoadAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(_root, "keyless.json")).Should().BeTrue();
        outcome.Data.Mode.Should().Be(ApprovalMode.Ask);
        outcome.Data.Limits.MaxCommandsPerResponse.Should().Be(5);
        outcome.Data.Browser.EnableDeepThink.Should().BeTrue();
        outcome.Data.Browser.EnableSearch.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_TolerantJson_CommentsCaseTrailingCommasStringEnums()
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            """
            {
              // approval mode
              "mode": "AutoSafe",   // string enum, lowercase key
              "Limits": {
                "maxCommandsPerResponse": 3,
                "MaxResponseChars": 50000,
              },
            }
            """);

        var outcome = await Service().LoadAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Mode.Should().Be(ApprovalMode.AutoSafe);
        outcome.Data.Limits.MaxCommandsPerResponse.Should().Be(3);
        outcome.Data.Limits.MaxResponseChars.Should().Be(50000);
        // untouched sections keep defaults
        outcome.Data.Limits.MaxIterationsPerTurn.Should().Be(25);
    }

    [Fact]
    public async Task LoadAsync_EmptyFile_ReturnsDefaults()
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"), "   ");

        var outcome = await Service().LoadAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Mode.Should().Be(ApprovalMode.Ask);
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_FailsWithPreciseMessage()
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"), "{ broken");

        var outcome = await Service().LoadAsync(_root);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.ConfigurationInvalid);
        outcome.Error.Message.Should().Contain("not valid JSON");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task LoadAsync_OutOfRangeValues_FailValidation(int maxCommands)
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            $$"""{ "Limits": { "MaxCommandsPerResponse": {{maxCommands}} } }""");

        var outcome = await Service().LoadAsync(_root);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.ConfigurationValidation);
        outcome.Error.Message.Should().Contain("MaxCommandsPerResponse");
    }

    [Fact]
    public async Task LoadAsync_MinAboveMaxDelay_Fails()
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            """{ "Browser": { "MinDelayMs": 2000, "MaxDelayMs": 100 } }""");

        var outcome = await Service().LoadAsync(_root);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Message.Should().Contain("MinDelayMs");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
