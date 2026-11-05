using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Tests.Fakes;
using ImanSoftware.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The composer-reset safety net: the chip selectors (AttachmentChips/Remove/Failed) are
/// unverified guesses, so a failed send with attachments marks the session suspect and the
/// next send reloads the chat page before trusting the composer. A send control that stays
/// disabled with no visible chips triggers one reload + one redelivery, then fails with
/// KA_COMPOSER_NOT_CLEAN.
/// </summary>
public class ComposerSuspectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-suspect-" + Guid.CreateVersion7().ToString("N"));

    public ComposerSuspectTests()
    {
        Directory.CreateDirectory(_root);
        RunGit("init -b main");
        RunGit("config user.email agent@example.com");
        RunGit("config user.name keyless-agent");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# workspace");
        File.WriteAllText(Path.Combine(_root, "tree-view.bat"), "@echo off\r\ntree /F /A > tree-view-result.txt\r\n");
        File.WriteAllText(Path.Combine(_root, "code-dump.bat"), "@echo off\r\necho ### code-dump-result > code-dump-result.md\r\n");
        RunGit("add -A");
        RunGit("commit -m init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* the bat processes may hold the folder briefly */ }
    }

    private void RunGit(string args)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit(30_000);
    }

    private static PlaywrightChatSession CreateSession(FakeChatPage page, int uploadWaitSeconds = 1, int replyTimeoutSeconds = 10)
    {
        var browserOptions = new BrowserOptions
        {
            MinDelayMs = 0,
            MaxDelayMs = 0,
            UploadWaitSeconds = uploadWaitSeconds,
            ReplyTimeoutSeconds = replyTimeoutSeconds,
        };

        return new(
            page,
            () => ValueTask.CompletedTask,
            new ChatSessionOptions
            {
                WorkspaceRoot = Path.Combine(Path.GetTempPath(), "ka-suspect-ws-" + Guid.CreateVersion7().ToString("N")),
                Browser = browserOptions,
            },
            new ReplyExtractor(),
            new RateLimiter(1000, 0, 0, new FakeDateTimeProvider()),
            new LoggerAdapter<PlaywrightChatSession>(NullLoggerFactory.Instance),
            ImanFileStorageFactory.CreateStorage(),
            pollDelayMs: 20);
    }

    private static SnapshotAttachment Attachment(string name, string content)
        => new(name, SnapshotAttachmentKind.CodeDump, content);

    private static void WireSuccessfulSend(FakeChatPage page, string key, string reply)
    {
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(50).ContinueWith(_ => page.SimulateAssistantReply(key, reply, 2));
        };
    }

    [Fact]
    public async Task FailedSendOffline_UndetectableChips_NextSendReloadsOnce_AndSucceeds()
    {
        var page = new FakeChatPage();
        // Chips ARE on the page (ChipsVisible) but the unverified selectors report none
        // (StaleChipCount 0) — exactly the "detection is not trusted" case.
        page.Online = false;
        page.ChipsVisible = true;
        var session = CreateSession(page, uploadWaitSeconds: 1);

        var first = await session.SendMessageAsync("first message", [Attachment("one.md", "one")]);
        first.Error.Code.Should().Be("KA_NETWORK_OFFLINE");
        page.ReloadCalls.Should().Be(0, "the failure itself never reloads");

        page.Online = true;
        WireSuccessfulSend(page, "442", "second reply");
        var second = await session.SendMessageAsync("second message", [Attachment("one.md", "one")]);

        second.IsSuccess.Should().BeTrue();
        page.ReloadCalls.Should().Be(1, "the suspect flag forces exactly one reload before the next send");
        page.LastInsertedText.Should().Be("second message");
    }

    [Fact]
    public async Task ConfirmedFirstSend_NextSendDoesNotReload()
    {
        var page = new FakeChatPage();
        WireSuccessfulSend(page, "442", "first reply");
        var session = CreateSession(page, uploadWaitSeconds: 1);
        var attachment = Attachment("one.md", "one");

        (await session.SendMessageAsync("first message", [attachment])).IsSuccess.Should().BeTrue();
        page.ReloadCalls.Should().Be(0);

        (await session.SendMessageAsync("second message", [attachment])).IsSuccess.Should().BeTrue();
        page.ReloadCalls.Should().Be(0, "a confirmed send proves the composer is clean — no reload");
    }

    [Fact]
    public async Task SendDisabledWithNoVisibleChips_ReloadsOnce_AndRedelivers()
    {
        var page = new FakeChatPage
        {
            SendEnabled = false,   // stuck disabled for the whole upload window
            ChipsVisible = false,  // ...and the (unverified) chip selectors see nothing
        };
        WireSuccessfulSend(page, "442", "reply after recovery");
        var session = CreateSession(page, uploadWaitSeconds: 1);
        var attachment = Attachment("code-dump-result.md", "dump");

        var outcome = await session.SendMessageAsync("message", [attachment]);

        outcome.IsSuccess.Should().BeTrue("the reload recovers the composer and the redelivered upload lands");
        page.ReloadCalls.Should().Be(1, "exactly one reload");
        page.UploadCalls.Should().Be(2, "the delivery is redone exactly once");
        page.LastInsertedText.Should().Be("message");
    }

    [Fact]
    public async Task SendDisabledAndReloadDoesNotHelp_FailsWithComposerNotClean()
    {
        var page = new FakeChatPage { FailReload = true, SendEnabled = false, ChipsVisible = false };
        var session = CreateSession(page, uploadWaitSeconds: 1);

        var outcome = await session.SendMessageAsync("message", [Attachment("one.md", "one")]);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("KA_COMPOSER_NOT_CLEAN");
        page.ReloadCalls.Should().Be(1, "one reload before giving up");
        page.UploadCalls.Should().Be(2, "the delivery was redone once before the failure");
    }

    [Fact]
    public async Task Reload_NeverHappensWhileGenerating()
    {
        var page = new FakeChatPage();
        var session = CreateSession(page, replyTimeoutSeconds: 1);

        // Message 1 fails (unconfirmed, with attachments) so the session becomes suspect...
        page.ChipsVisible = true;
        page.OnSent = () => { };
        await session.SendMessageAsync("first", [Attachment("one.md", "one")]);
        page.ReloadCalls.Should().Be(0);

        // ...and message 2 starts while the fake is STILL "generating": the reload must wait
        // out the generating indicator (ReplyTimeoutSeconds = 1) before touching the composer.
        page.StopVisible = true;
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(50).ContinueWith(_ => page.SimulateAssistantReply("443", "late reply", 2));
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await session.SendMessageAsync("second", []);
        sw.Stop();

        outcome.IsSuccess.Should().BeTrue();
        page.ReloadCalls.Should().Be(1);
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1),
            "the reload waits out the generating indicator before touching the composer");
    }

    [Fact]
    public async Task EnterRetry_NeverReloads()
    {
        var page = new FakeChatPage();
        var session = CreateSession(page);
        page.ChipsVisible = true;
        page.OnSent = () => { }; // nothing lands: unconfirmed → exactly one Enter retry

        var outcome = await session.SendMessageAsync("message", [Attachment("one.md", "one")]);

        outcome.IsFailure.Should().BeTrue();
        page.Sends.Select(s => s.Method).Should().Equal("click", "enter");
        page.ReloadCalls.Should().Be(0, "the Enter retry inside one send attempt never reloads");
    }
}
