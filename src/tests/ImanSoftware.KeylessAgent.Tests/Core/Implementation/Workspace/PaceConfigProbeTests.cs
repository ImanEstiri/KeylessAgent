
using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Workspace;

public class PaceConfigProbeTests
{
    [Fact]
    public async Task Config_ParsesAgentPace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ka-pacecfg-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "keyless.json"),
            "{ \"SchemaVersion\": 2, \"Agent\": { \"Pace\": \"Step\" }, \"Limits\": { \"MaxCommandsPerResponse\": 5 } }");
        var outcome = await new ConfigurationService().LoadAsync(root);
        outcome.IsSuccess.Should().BeTrue(outcome.IsFailure ? outcome.Error.Message : "");
        outcome.Data.Agent.Pace.Should().Be(AgentPace.Step);
    }
}
