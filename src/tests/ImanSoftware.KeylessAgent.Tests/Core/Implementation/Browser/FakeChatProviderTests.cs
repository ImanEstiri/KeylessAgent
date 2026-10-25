using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

public class FakeChatProviderTests
{
    [Fact]
    public async Task FakeChat_YieldsScriptedRepliesInOrder_AndRecordsSends()
    {
        var provider = new FakeChatProvider("first", "second");
        var sessionOutcome = await provider.StartSessionAsync(new ChatSessionOptions());
        sessionOutcome.IsSuccess.Should().BeTrue();
        var session = sessionOutcome.Data;

        await session.SendMessageAsync("hello", []);
        (await session.WaitForReplyAsync()).Data.Should().Be("first");
        await session.SendMessageAsync("again", []);
        (await session.WaitForReplyAsync()).Data.Should().Be("second");

        provider.Sent.Should().HaveCount(2);
        provider.Sent[0].Text.Should().Be("hello");
        provider.RemainingReplies.Should().Be(0);
    }

    [Fact]
    public async Task FakeChat_ExhaustedReplies_Fail()
    {
        var provider = new FakeChatProvider("only");
        var session = (await provider.StartSessionAsync(new ChatSessionOptions())).Data;

        await session.WaitForReplyAsync();
        var outcome = await session.WaitForReplyAsync();

        outcome.IsFailure.Should().BeTrue();
    }
}

public class SelectorsCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-selectors-" + Guid.CreateVersion7().ToString("N"));

    public SelectorsCatalogTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Load_MissingFile_WritesEmbeddedDefaultAndUsesIt()
    {
        var outcome = SelectorsCatalog.Load(_root, "selectors.json");

        outcome.IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(_root, "selectors.json")).Should().BeTrue();
        outcome.Data.InputBox.Should().Contain("""textarea[placeholder="Message DeepSeek"]""");
        outcome.Data.FinalAnswer.Should().Contain(".ds-markdown.ds-assistant-message-main-content");
        outcome.Data.DeepThinkToggle.Should().NotBeEmpty();
        outcome.Data.SearchToggle.Should().NotBeEmpty();
    }

    [Fact]
    public void Load_ExistingFile_IsPreferred()
    {
        File.WriteAllText(Path.Combine(_root, "selectors.json"),
            """{ "InputBox": ["textarea#custom"], "SendButton": ["#send"], "FinalAnswer": [".answer"] }""");

        var outcome = SelectorsCatalog.Load(_root, "selectors.json");

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.InputBox.Should().BeEquivalentTo(["textarea#custom"]);
    }

    [Fact]
    public void Load_InvalidJson_FailsWithPreciseMessage()
    {
        File.WriteAllText(Path.Combine(_root, "selectors.json"), "{ not json ]");

        var outcome = SelectorsCatalog.Load(_root, "selectors.json");

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Message.Should().Contain("not valid JSON");
    }

    [Fact]
    public void Load_MissingRequiredSelectors_Fails()
    {
        File.WriteAllText(Path.Combine(_root, "selectors.json"),
            """{ "InputBox": ["a"], "SendButton": [], "FinalAnswer": [] }""");

        var outcome = SelectorsCatalog.Load(_root, "selectors.json");

        outcome.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Load_TolerantJson_CommentsCaseAndTrailingCommas()
    {
        File.WriteAllText(Path.Combine(_root, "selectors.json"),
            """
            {
              // main input
              "inputbox": ["textarea#x",],
              "sendbutton": ["#send"],
              "finalanswer": [".answer",]
            }
            """);

        var outcome = SelectorsCatalog.Load(_root, "selectors.json");

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.InputBox.Should().Contain("textarea#x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
