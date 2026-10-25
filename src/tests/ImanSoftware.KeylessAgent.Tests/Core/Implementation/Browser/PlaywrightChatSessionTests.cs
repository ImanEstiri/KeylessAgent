using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Tests.Fakes;
using ImanSoftware.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// Reply-identity tests for the chat session: the previous assistant reply must never be
/// returned while the new one is still starting, even when its text is identical.
/// </summary>
public class PlaywrightChatSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-session-" + Guid.CreateVersion7().ToString("N"));

    public PlaywrightChatSessionTests()
    {
        Directory.CreateDirectory(_root);
    }

    private PlaywrightChatSession CreateSession(FakeChatPage page, int sendConfirmSeconds = 5, int uploadWaitSeconds = 5)
        => new(
            page,
            () => ValueTask.CompletedTask,
            new ChatSessionOptions
            {
                WorkspaceRoot = _root,
                Browser = new BrowserOptions
                {
                    MinDelayMs = 0,
                    MaxDelayMs = 0,
                    SendConfirmSeconds = sendConfirmSeconds,
                    UploadWaitSeconds = uploadWaitSeconds,
                    ReplyTimeoutSeconds = 10,
                },
            },
            new ReplyExtractor(),
            new RateLimiter(1000, 0, 0, new FakeDateTimeProvider()),
            new LoggerAdapter<PlaywrightChatSession>(NullLoggerFactory.Instance),
            ImanFileStorageFactory.CreateStorage(),
            pollDelayMs: 20);

    private static void ArrangeFirstReplyDelivered(FakeChatPage page)
    {
        page.SimulateAssistantReply("441", "first reply", 1);
        page.ItemCount = 2;
    }

    [Fact]
    public async Task SecondTurn_DoesNotReturnTheFirstReply_WhileTheNewOneIsStillStarting()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () =>
        {
            // The send lands, generating starts — but the new reply only appears after a delay,
            // while the OLD assistant message is still the newest one in the DOM.
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(300).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "second reply", 2));
        };
        var session = CreateSession(page);

        (await session.SendMessageAsync("next request", [])).IsSuccess.Should().BeTrue();
        var reply = await session.WaitForReplyAsync();

        reply.IsSuccess.Should().BeTrue("the new reply must arrive, not a timeout");
        reply.Data.Should().Be("second reply");
        reply.Data.Should().NotBe("first reply");
    }

    [Fact]
    public async Task IdenticalReplyTextTwiceInARow_IsAccepted_WhenTheMessageKeyChanged()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        var replyNumber = 0;
        page.OnSent = () =>
        {
            replyNumber++;
            var key = $"44{replyNumber + 1}";
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(150).ContinueWith(_ =>
                page.SimulateAssistantReply(key, "identical reply text", 2));
        };
        var session = CreateSession(page);

        (await session.SendMessageAsync("request one", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).Data.Should().Be("identical reply text");

        (await session.SendMessageAsync("request two", [])).IsSuccess.Should().BeTrue();
        var second = await session.WaitForReplyAsync();

        second.IsSuccess.Should().BeTrue("same text with a NEW message key is a valid reply");
        second.Data.Should().Be("identical reply text");
    }

    [Fact]
    public async Task SendNotConfirmed_RetriesOnce_ThenFailsWithSendNotConfirmed()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () => { }; // nothing lands: no new item, input keeps the text, no generating
        var session = CreateSession(page, sendConfirmSeconds: 1);

        var outcome = await session.SendMessageAsync("request", []);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("KA_SEND_NOT_CONFIRMED");

        // primary attempt (click) + one retry (click, then Enter)
        page.Sends.Select(s => s.Method).Should().Equal("click", "click", "enter");
    }

    [Fact]
    public async Task UploadStillPending_DelaysTheSendUntilChipsAndEnabledSend()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.ChipsVisible = false;
        page.SendEnabled = false;
        _ = Task.Delay(300).ContinueWith(_ =>
        {
            page.ChipsVisible = true;
            page.SendEnabled = true;
        });
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(150).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "reply after upload", 2));
        };
        var session = CreateSession(page, uploadWaitSeconds: 5);
        var attachment = new SnapshotAttachment("tree-view-result.txt", SnapshotAttachmentKind.TreeView, "tree content");

        var outcome = await session.SendMessageAsync("request with attachment", [attachment]);

        outcome.IsSuccess.Should().BeTrue();
        page.Sends.Should().NotBeEmpty();
        page.Sends[0].ChipsVisible.Should().BeTrue("the send click must wait until the upload settled");
        (await session.WaitForReplyAsync()).Data.Should().Be("reply after upload");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
