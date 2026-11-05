using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Wpf.Services;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Services;

public sealed class EventPresenterTests
{
    public static TheoryData<AgentEventType, string, string, EventKind> Table() => new()
    {
        { AgentEventType.SessionStarting, "Preparing the session", "●", EventKind.Step },
        { AgentEventType.ConnectingToBrowser, "Connecting to Chrome", "●", EventKind.Step },
        { AgentEventType.OpeningChat, "Opening the chat", "●", EventKind.Step },
        { AgentEventType.UploadingAttachments, "Uploading project snapshot", "▲", EventKind.Step },
        { AgentEventType.SendingMessage, "Sending your request", "▶", EventKind.Step },
        { AgentEventType.WaitingForReply, "Waiting for the model", "●", EventKind.Waiting },
        { AgentEventType.ValidatingReply, "Checking the reply", "✓", EventKind.Step },
        { AgentEventType.RequestingResend, "Asking the model to fix its reply", "⚠", EventKind.Warning },
        { AgentEventType.ExecutingCommand, "Running a command", "▶", EventKind.Step },
        { AgentEventType.AskingUser, "Waiting for you", "●", EventKind.Waiting },
        { AgentEventType.SendingResults, "Sending results to the model", "▶", EventKind.Step },
        { AgentEventType.RefreshingSnapshot, "Updating the project snapshot", "●", EventKind.Step },
        { AgentEventType.TurnFinished, "Finished", "✓", EventKind.Success },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Present_MapsEveryEventOfTheGuideTable(AgentEventType type, string label, string symbol, EventKind kind)
    {
        var presentation = EventPresenter.Present(new AgentProgressEvent(type, "detail"));

        presentation.Label.Should().Be(label);
        presentation.Symbol.Should().Be(symbol);
        presentation.Kind.Should().Be(kind);
    }

    [Fact]
    public void Present_NullEvent_Throws()
        => FluentActions.Invoking(() => EventPresenter.Present(null!)).Should().Throw<ArgumentNullException>();

    [Theory]
    [InlineData("#1 RunCommand mkdir y", 1, "RunCommand", "mkdir y")]
    [InlineData("#12 WriteFile src/a.cs", 12, "WriteFile", "src/a.cs")]
    [InlineData("#3 RunCommand git commit -m \"fix: x\"", 3, "RunCommand", "git commit -m \"fix: x\"")]
    public void TryParseCommandDetail_ParsesValidCommandDetails(string detail, int number, string kind, string target)
    {
        var parsed = EventPresenter.TryParseCommandDetail(detail);

        (parsed?.Number, parsed?.Kind, parsed?.Target).Should().Be((number, kind, target));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text without a command")]
    [InlineData("#nope RunCommand mkdir")]
    [InlineData("#1 RunCommand")]
    [InlineData("1 RunCommand mkdir y")]
    public void TryParseCommandDetail_ReturnsNullForUnexpectedText(string detail)
        => EventPresenter.TryParseCommandDetail(detail).Should().BeNull();

    [Fact]
    public void TryParseCommandDetail_NullDetail_ReturnsNull()
        => EventPresenter.TryParseCommandDetail(null).Should().BeNull();
}
