using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Agent;

public class SystemPromptBuilderTests
{
    [Fact]
    public void Build_ContainsTheMissingToolsRule()
    {
        var prompt = SystemPromptBuilder.Build(new KeylessOptions());

        prompt.IsSuccess.Should().BeTrue();
        prompt.Data.Should().Contain(
            "Never retry a tool the Environment lists as missing",
            "the model must not loop on tools the probe reported as missing");
        prompt.Data.Should().Contain("say so in a `say` block");
    }

    [Theory]
    [InlineData(AgentPace.Step, "Work ONE step at a time", "Limit: 1")]
    [InlineData(AgentPace.Balanced, "group at most 3 related commands", "Limit: 3")]
    [InlineData(AgentPace.Batch, "Limit: 5")]
    public void Build_InjectsThePaceRuleForTheConfiguredPace(AgentPace pace, params string[] expectedParts)
    {
        var options = new KeylessOptions
        {
            Agent = new AgentOptions { Pace = pace },
            Limits = new LimitsOptions { MaxCommandsPerResponse = 5 },
        };

        var prompt = SystemPromptBuilder.Build(options);

        foreach (var part in expectedParts)
            prompt.Data.Should().Contain(part);
    }

    [Fact]
    public void Build_ContainsThePlanRule()
    {
        var prompt = SystemPromptBuilder.Build(new KeylessOptions());

        prompt.Data.Should().Contain("short numbered plan, then execute step 1");
    }
}
