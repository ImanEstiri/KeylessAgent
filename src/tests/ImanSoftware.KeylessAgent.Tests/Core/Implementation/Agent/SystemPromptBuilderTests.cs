using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
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
}
