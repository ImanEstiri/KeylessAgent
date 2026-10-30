using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Tests.Fakes;
using ImanSoftware.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// ReplySource strategies (interception / clipboard / none) and validation of the copied text.
/// </summary>
public class ReplyReaderTests
{
    private static ReplyReadContext Context(string dom, string? reasoning, string? captured)
        => new(dom, reasoning, _ => Task.FromResult<string?>(captured));

    private static PlaywrightChatSession CreateSession(FakeChatPage page, ReplySourceMode mode)
        => new(
            page,
            () => ValueTask.CompletedTask,
            new ChatSessionOptions
            {
                WorkspaceRoot = Path.Combine(Path.GetTempPath(), "ka-reply-" + Guid.CreateVersion7().ToString("N")),
                Browser = new BrowserOptions
                {
                    MinDelayMs = 0, MaxDelayMs = 0,
                    SendConfirmSeconds = 5, UploadWaitSeconds = 5,
                    ReplyTimeoutSeconds = 10,
                    ReplySource = mode,
                },
            },
            new ReplyExtractor(),
            new RateLimiter(1000, 0, 0, new FakeDateTimeProvider()),
            new LoggerAdapter<PlaywrightChatSession>(NullLoggerFactory.Instance),
            ImanFileStorageFactory.CreateStorage(),
            pollDelayMs: 20);

    private static void ArrangeFirstReply(FakeChatPage page)
    {
        page.SimulateAssistantReply("441", "first reply", 1);
        page.ItemCount = 2;
    }

    // ---------- ReplyReader validation ----------

    [Fact]
    public void Validate_AcceptsPlainMarkdownCopy()
    {
        var dom = "Final answer intro. Use editfile for small changes.";
        var copied = "**Final answer intro.** Use _editfile_ for small changes.";

        var (isValid, _) = ReplyReader.Validate(copied, dom, null);

        isValid.Should().BeTrue("markdown syntax is stripped before comparing");
    }

    [Fact]
    public void Validate_AcceptsAgentScriptReplyWithNestedFences()
    {
        var dom = "@@newfile src/README.md\n<<<CONTENT\n# Title\n\n```bash\ndotnet build\n```\nCONTENT>>>\n@@end\n@@eof";
        var copied = "```agent\n@@newfile src/README.md\n<<<CONTENT\n# Title\n\n```bash\ndotnet build\n```\nCONTENT>>>\n@@end\n@@eof\n```";

        var (isValid, summary) = ReplyReader.Validate(copied, dom, null);

        isValid.Should().BeTrue("the copy is raw markdown with preserved markers: {0}", summary);
    }

    [Fact]
    public void Validate_RejectsReasoningContaminatedCopy()
    {
        var dom = "The final answer text.";
        var reasoning = "I should analyze the request and then decide what to do next.";
        var copied = "I should analyze the request and then decide what to do next. The final answer text.";

        var (isValid, summary) = ReplyReader.Validate(copied, dom, reasoning);

        isValid.Should().BeFalse("reasoning leaked into the copy: {0}", summary);
    }

    [Fact]
    public void Validate_RejectsTruncatedCopy()
    {
        var dom = new string('a', 1000) + " final words here";
        var copied = "aaa";

        var (isValid, _) = ReplyReader.Validate(copied, dom, null);

        isValid.Should().BeFalse("a copy under 50% of the DOM text is truncated");
    }

    [Fact]
    public void NormalizeForCompare_StripsMarkdownAndPunctuation()
    {
        var normalized = ReplyReader.NormalizeForCompare("**Hello**, _world_! `code` > quote");

        normalized.Should().Be("hello world code quote");
    }

    // ---------- session strategies ----------

    [Fact]
    public async Task Auto_InterceptionCaptures_CopyBecomesParserInputAndOsClipboardUntouched()
    {
        var page = new FakeChatPage
        {
            CopyButtonVisible = true,
            CapturedReply = "@@done\n<<<MSG\ncopied done\nMSG>>>\n@@end\n@@eof\n",
            ReasoningText = "I think the task is easy.",
        };
        ArrangeFirstReply(page);
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
            {
                page.SimulateAssistantReply("442", page.CapturedReply ?? "dom reply", 2);
                page.ReasoningText = "I think the task is easy.";
                page.CapturedReply = page.NewestAssistantText;
            });
        };
        var session = CreateSession(page, ReplySourceMode.Auto);

        (await session.SendMessageAsync("request", [])).IsSuccess.Should().BeTrue();
        var reply = await session.WaitForReplyAsync();

        reply.IsSuccess.Should().BeTrue();
        reply.Data.Should().Contain("copied done");
        page.InterceptionPrepared.Should().BeTrue("interception must be installed before clicking");
        page.LastCaptureStrategy.Should().Be("interception");
        page.OsClipboardUntouched.Should().BeTrue("interception never touches the OS clipboard");
        page.LastCopyClickedKey.Should().Be("442", "only the NEWEST message's Copy control is clicked");
        session.LastReplyOrigin.Should().Be("Copy");
    }

    [Fact]
    public async Task Auto_InterceptionFails_ClipboardPath_RestoresPreviousClipboard()
    {
        var page = new FakeChatPage
        {
            CopyButtonVisible = true,
            ForceClipboardStrategy = true, // interception produced nothing
            CapturedReply = "clipboard captured text",
            OsClipboard = "user-clipboard-content",
        };
        ArrangeFirstReply(page);
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ => page.SimulateAssistantReply("442", "dom text", 2));
        };
        var session = CreateSession(page, ReplySourceMode.Auto);

        (await session.SendMessageAsync("request", [])).IsSuccess.Should().BeTrue();
        var reply = await session.WaitForReplyAsync();

        reply.IsSuccess.Should().BeTrue();
        page.LastCaptureStrategy.Should().Be("clipboard");
        page.OsClipboardUntouched.Should().BeTrue("the previous clipboard text is restored in a finally block");
        // validation compares against DOM: "clipboard captured text" differs from "dom text"…
        // containment fails but similarity may be low too; AcceptCopy property decides:
        // (The fake returns the text as-is; this test only verifies strategy + restore.)
    }

    [Fact]
    public async Task Auto_BothStrategiesFail_FallsBackToDom()
    {
        var page = new FakeChatPage
        {
            CopyButtonVisible = false, // no Copy control at all
            CapturedReply = null,
        };
        ArrangeFirstReply(page);
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ => page.SimulateAssistantReply("442", "dom fallback reply", 2));
        };
        var session = CreateSession(page, ReplySourceMode.Auto);

        (await session.SendMessageAsync("request", [])).IsSuccess.Should().BeTrue();
        var reply = await session.WaitForReplyAsync();

        reply.IsSuccess.Should().BeTrue("Auto falls back to the DOM reader");
        reply.Data.Should().Be("dom fallback reply");
        session.LastReplyOrigin.Should().Be("Dom: copy button not found");
    }

    [Fact]
    public async Task Copy_BothStrategiesFail_FailsWithReplyTimeout()
    {
        var page = new FakeChatPage
        {
            CopyButtonVisible = true,
            CapturedReply = null, // both strategies produce nothing
        };
        ArrangeFirstReply(page);
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ => page.SimulateAssistantReply("442", "dom text", 2));
        };
        var session = CreateSession(page, ReplySourceMode.Copy);

        (await session.SendMessageAsync("request", [])).IsSuccess.Should().BeTrue();
        var reply = await session.WaitForReplyAsync();

        reply.IsFailure.Should().BeTrue("Copy mode never falls back silently");
        reply.Error.Code.Should().Be("KA_REPLY_TIMEOUT");
    }

    [Fact]
    public async Task Dom_NeverTouchesTheCopyPath()
    {
        var page = new FakeChatPage();
        ArrangeFirstReply(page);
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ => page.SimulateAssistantReply("442", "dom only", 2));
        };
        var session = CreateSession(page, ReplySourceMode.Dom);

        (await session.SendMessageAsync("request", [])).IsSuccess.Should().BeTrue();
        var reply = await session.WaitForReplyAsync();

        reply.Data.Should().Be("dom only");
        page.InterceptionPrepared.Should().BeFalse("Dom mode must not install interception");
        session.LastReplyOrigin.Should().Be("Dom: Browser.ReplySource=Dom");
    }
}
