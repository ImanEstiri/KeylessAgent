using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Git;

public class BranchGuardTests
{
    private readonly GitOptions _options = new();
    private readonly FakeGitService _git = new();

    [Theory]
    [InlineData("main")]
    [InlineData("MASTER")]
    [InlineData("master")]
    [InlineData("release/1.2")]
    [InlineData("releases")]
    [InlineData("release-2026-04")]
    public void IsProtected_ProtectedBranches_AreBlocked(string branch)
    {
        new BranchGuard(_git).IsProtected(branch, _options).Should().BeTrue();
    }

    [Theory]
    [InlineData("agent/20260101-0930-fix-login")]
    [InlineData("feature/login")]
    [InlineData("develop")]
    [InlineData("relegation")]
    public void IsProtected_WorkingBranches_AreAllowed(string branch)
    {
        new BranchGuard(_git).IsProtected(branch, _options).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureModifiable_OnProtectedBranch_FailsWithBranchProtected()
    {
        _git.CurrentBranch = "main";
        var guard = new BranchGuard(_git);

        var outcome = await guard.EnsureModifiableAsync("C:\\ws", _options);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.BranchProtected);
    }

    [Fact]
    public async Task EnsureModifiable_OnAgentBranch_Succeeds()
    {
        _git.CurrentBranch = "agent/20260101-0930-task";
        var guard = new BranchGuard(_git);

        var outcome = await guard.EnsureModifiableAsync("C:\\ws", _options);

        outcome.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureModifiable_OutsideRepository_FailsWithNotARepository()
    {
        _git.IsRepository = false;
        var guard = new BranchGuard(_git);

        var outcome = await guard.EnsureModifiableAsync("C:\\ws", _options);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.NotAGitRepository);
    }
}

public class BranchNameBuilderTests
{
    private readonly BranchNameBuilder _builder = new(new FakeDateTimeProvider());

    [Fact]
    public void Build_UsesFakeClockAndRequestSlug()
    {
        var name = _builder.Build("Fix the login bug", new GitOptions());

        name.Should().Be("agent/20260101-0930-fix-the-login-bug");
    }

    [Fact]
    public void Build_EmptyRequest_FallsBackToTask()
    {
        _builder.Build("", new GitOptions()).Should().Be("agent/20260101-0930-task");
    }

    [Fact]
    public void Build_NonLatinRequest_FallsBackToTask()
    {
        _builder.Build("درخواست فارسی", new GitOptions()).Should().Be("agent/20260101-0930-task");
    }

    [Fact]
    public void Build_SymbolsAreStripped()
    {
        _builder.Build("Fix: remove *** all /// symbols!!!", new GitOptions())
            .Should().Be("agent/20260101-0930-fix-remove-all-symbols");
    }

    [Fact]
    public void Build_VeryLongRequest_IsCappedToFortyChars()
    {
        var name = _builder.Build(new string('a', 500), new GitOptions());

        var slug = name["agent/20260101-0930-".Length..];
        slug.Length.Should().Be(40);
    }

    [Fact]
    public void Build_CustomPrefixIsUsed()
    {
        _builder.Build("task", new GitOptions { BranchPrefix = "ka/" })
            .Should().Be("ka/20260101-0930-task");
    }
}
