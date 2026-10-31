using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Wpf.Services;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Services;

public sealed class SettingsGroupMapperTests
{
    [Fact]
    public void Map_ProducesTheGuideGroupsInOrder()
    {
        var groups = SettingsGroupMapper.Map(new KeylessOptions());

        groups.Select(group => group.Title).Should().Equal("Mode", "Browser", "Limits", "Snapshot", "Git", "Commands");
    }

    [Fact]
    public void Map_KeepsDefaultsReadable()
    {
        var groups = SettingsGroupMapper.Map(new KeylessOptions());

        groups.Single(group => group.Title == "Mode").Rows.Should().ContainSingle(row => row.Value == "Ask");
        var browser = groups.Single(group => group.Title == "Browser");
        browser.Rows.Should().Contain(row => row.Key == "ChromeDebugPort" && row.Value == "9222");
        browser.Rows.Should().Contain(row => row.Key == "AutoLaunchChrome" && row.Value == "True");
        browser.Rows.Should().Contain(row => row.Key == "ReplySource" && row.Value == "Auto");
        var limits = groups.Single(group => group.Title == "Limits");
        limits.Rows.Should().Contain(row => row.Key == "MaxCommandsPerResponse" && row.Value == "5");
        var git = groups.Single(group => group.Title == "Git");
        git.Rows.Should().Contain(row => row.Key == "BranchPrefix" && row.Value == "agent/");
        git.Rows.Should().Contain(row => row.Key == "ProtectedBranchPatterns" && row.Value.Contains("main"));
        var commands = groups.Single(group => group.Title == "Commands");
        commands.Rows.Should().Contain(row => row.Key == "Allowlist" && row.Value.Contains("dotnet"));
        commands.Rows.Should().Contain(row => row.Key == "BatchFailureMode" && row.Value == "StopOnMutationFailure");
    }

    [Fact]
    public void Map_PropagatesCustomValues()
    {
        var options = new KeylessOptions
        {
            Mode = Abstractions.Models.Enums.ApprovalMode.AutoSafe,
            Browser = new BrowserOptions { ChromeDebugPort = 9333 },
            Limits = new LimitsOptions { MaxCommandsPerResponse = 7 },
        };

        var groups = SettingsGroupMapper.Map(options);

        groups.Single(group => group.Title == "Mode").Rows[0].Value.Should().Be("AutoSafe");
        groups.Single(group => group.Title == "Browser").Rows.Should().Contain(row => row.Key == "ChromeDebugPort" && row.Value == "9333");
        groups.Single(group => group.Title == "Limits").Rows.Should().Contain(row => row.Key == "MaxCommandsPerResponse" && row.Value == "7");
    }

    [Fact]
    public void Map_EmptyUserDataDir_ShowsDefaultProfileNote()
    {
        var groups = SettingsGroupMapper.Map(new KeylessOptions());

        groups.Single(group => group.Title == "Browser").Rows
            .Should().Contain(row => row.Key == "UserDataDir" && row.Value.Contains("default profile"));
    }

    [Fact]
    public void Map_NullOptions_Throws()
        => FluentActions.Invoking(() => SettingsGroupMapper.Map(null!)).Should().Throw<ArgumentNullException>();
}
