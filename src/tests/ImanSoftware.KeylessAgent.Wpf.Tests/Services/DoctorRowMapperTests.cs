using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Services;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Services;

public sealed class DoctorRowMapperTests
{
    private static ChatDiagnosticsReport Report() => new(
        PageUrl: "https://chat.deepseek.com/",
        LoggedIn: true,
        DeepThinkPressed: "true",
        SearchPressed: "false",
        InputAcceptsText: true,
        Checks:
        [
            new SelectorCheck("InputBox", SelectorCheckStatus.Matched, 0, 1, "fallback #0 matched 1 element"),
            new SelectorCheck("StopButton", SelectorCheckStatus.NotApplicableNow, -1, 0, "no generation running"),
            new SelectorCheck("SendButton", SelectorCheckStatus.NotFound, -1, 0, "no fallback matched"),
        ],
        AllRequiredMatched: false,
        CapturedFiles: []);

    [Fact]
    public void MapChecks_MapsEveryStatusToItsChipText()
    {
        var rows = DoctorRowMapper.MapChecks(Report().Checks);

        rows.Should().HaveCount(3);
        rows[0].State.Should().Be("MATCHED");
        rows[0].Fallback.Should().Be("#0");
        rows[0].Elements.Should().Be("1");
        rows[1].State.Should().Be("NOT APPLICABLE NOW");
        rows[1].Fallback.Should().Be("-");
        rows[1].Elements.Should().Be("-");
        rows[2].State.Should().Be("NOT FOUND");
        rows[2].Detail.Should().Contain("no fallback matched");
    }

    [Fact]
    public void MapSummary_OrderAndHealthMatchTheCliOutput()
    {
        var rows = DoctorRowMapper.MapSummary(Report());

        rows.Select(row => row.Label).Should().Equal(
            "Logged in", "DeepThink pressed", "Search pressed", "Input accepts text", "All required matched");
        rows[0].Ok.Should().BeTrue();
        rows[1].Ok.Should().BeTrue();
        rows[1].Value.Should().Be("true");
        rows[2].Ok.Should().BeFalse();
        rows[2].Value.Should().Be("false");
        rows[4].Value.Should().Be("NO");
    }

    [Fact]
    public void MapSummary_MissingToggle_ShowsNotFoundAndUnhealthy()
    {
        var report = Report() with { DeepThinkPressed = null, SearchPressed = null };

        var rows = DoctorRowMapper.MapSummary(report);

        rows[1].Value.Should().Be("not found");
        rows[1].Ok.Should().BeFalse();
        rows[2].Value.Should().Be("not found");
    }

    [Fact]
    public void MapChecks_NullInput_Throws()
        => FluentActions.Invoking(() => DoctorRowMapper.MapChecks(null!)).Should().Throw<ArgumentNullException>();

    [Fact]
    public void MapSummary_NullReport_Throws()
        => FluentActions.Invoking(() => DoctorRowMapper.MapSummary(null!)).Should().Throw<ArgumentNullException>();
}
