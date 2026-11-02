using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
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

    private PlaywrightChatSession CreateSession(FakeChatPage page, int sendConfirmSeconds = 5, int uploadWaitSeconds = 5, Func<BrowserOptions, BrowserOptions>? configureBrowser = null, string? resumeChatUrl = null, ITimingRecorder? timings = null, Func<string, object, Task>? logEvent = null)
    {
        var browserOptions = new BrowserOptions
        {
            MinDelayMs = 0,
            MaxDelayMs = 0,
            SendConfirmSeconds = sendConfirmSeconds,
            UploadWaitSeconds = uploadWaitSeconds,
            ReplyTimeoutSeconds = 10,
        };
        if (configureBrowser is not null)
            browserOptions = configureBrowser(browserOptions);

        return new(
            page,
            () => ValueTask.CompletedTask,
            new ChatSessionOptions
            {
                WorkspaceRoot = _root,
                Browser = browserOptions,
                ResumeChatUrl = resumeChatUrl,
                Timings = timings,
                LogEvent = logEvent,
            },
            new ReplyExtractor(),
            new RateLimiter(1000, 0, 0, new FakeDateTimeProvider()),
            new LoggerAdapter<PlaywrightChatSession>(NullLoggerFactory.Instance),
            ImanFileStorageFactory.CreateStorage(),
            pollDelayMs: 20);
    }

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
    public async Task SecondTurn_CopyCapture_ReturnsTheNewReply_NotTheStaleCapturedText()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);

        // The Copy-capture path runs on every reply: Auto mode + a visible Copy control.
        page.CopyButtonVisible = true;
        page.CapturedReply = "A";
        var turn = 0;
        page.OnSent = () =>
        {
            turn++;
            var key = $"44{turn + 1}";
            var text = turn == 1 ? "A" : "B";
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
            {
                page.CapturedReply = text;
                page.SimulateAssistantReply(key, text, turn + 1);
            });
        };
        var session = CreateSession(page);

        (await session.SendMessageAsync("first request", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).Data.Should().Be("A");

        (await session.SendMessageAsync("second request", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).Data.Should().Be("B");
    }

    [Fact]
    public async Task FirstSendOfNewChat_SetsToggles_SecondSendDoesNotTouchThem()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);

        // Config: DeepThink on (default true), Search off. The fake starts opposite on both.
        // The first two selectors of each toggle array are shared, so the per-toggle state is
        // keyed by each toggle's own text selector (the third fallback).
        page.ToggleStates[".ds-toggle-button:has-text(\"DeepThink\")"] = "false";
        page.ToggleStates[".ds-toggle-button:has-text(\"Search\")"] = "true";

        var sends = 0;
        page.OnSent = () =>
        {
            sends++;
            var key = $"44{sends + 1}";
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(50).ContinueWith(_ =>
                page.SimulateAssistantReply(key, $"reply {sends}", sends + 1));
        };
        var session = CreateSession(page, configureBrowser: b => b with { EnableSearch = false });

        (await session.SendMessageAsync("first request", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).IsSuccess.Should().BeTrue();

        (await session.SendMessageAsync("second request", [])).IsSuccess.Should().BeTrue();

        page.ClickedToggles.Should().HaveCount(2, "both toggles are clicked once on the first message, never again");
        page.ClickedToggles.Should().Contain(".ds-toggle-button:has-text(\"DeepThink\")");
        page.ClickedToggles.Should().Contain(".ds-toggle-button:has-text(\"Search\")");
    }

    [Fact]
    public async Task ResumedChat_DoesNotTouchToggles()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.ToggleStates[".ds-toggle-button:has-text(\"DeepThink\")"] = "false";
        page.ToggleStates[".ds-toggle-button:has-text(\"Search\")"] = "true";
        page.OnSent = () => page.SimulateUserMessageAccepted();

        var session = CreateSession(page,
            configureBrowser: b => b with { EnableSearch = false },
            resumeChatUrl: "https://chat.deepseek.com/a/existing");

        (await session.SendMessageAsync("request on resumed chat", [])).IsSuccess.Should().BeTrue();

        page.ClickedToggles.Should().BeEmpty("a resumed chat keeps whatever its toggles were");
    }

    [Fact]
    public async Task WaitForReplyAsync_ChatLimitNotice_ReturnsChatExhausted()
    {
        var page = new FakeChatPage { ChatLimitVisible = true };
        ArrangeFirstReplyDelivered(page);
        var session = CreateSession(page);

        var reply = await session.WaitForReplyAsync();

        reply.IsFailure.Should().BeTrue();
        reply.Error.Code.Should().Be(ErrorCodes.ChatExhausted);
        reply.Error.Message.Should().Contain("length limit");
    }

    [Fact]
    public async Task SessionTiming_PhasesAreRecordedThroughTheFakePage()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.CopyButtonVisible = true;
        page.CapturedReply = "captured reply";
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "captured reply", 2));
        };
        var recorder = new TimingRecorder();
        var session = CreateSession(page, configureBrowser: b => b with { EnableSearch = false }, timings: recorder);

        (await session.SendMessageAsync("request one", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).IsSuccess.Should().BeTrue();

        var phases = recorder.Entries.Select(e => e.Phase).ToList();
        phases.Should().Contain("toggles", "the new chat applies the DeepThink/Search toggles");
        phases.Should().Contain("upload", "attachment delivery is timed");
        phases.Should().Contain("send", "the send attempt is timed");
        phases.Should().Contain("send-confirm#1", "the send confirmation is timed once per attempt");
        phases.Should().Contain("first-token", "the wait until generation starts is timed");
        phases.Should().Contain("completion", "the wait until the reply is stable is timed");
        phases.Should().Contain("copy", "the reply capture is timed");
    }

    [Fact]
    public async Task UploadWithoutSign_IsNotPastedOnTop()
    {
        // SetInputFiles succeeds (fake default) but NO upload sign ever appears: the content
        // must be treated as attached, never pasted on top of a possibly-invisible upload.
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () => page.SimulateUserMessageAccepted();
        var session = CreateSession(page, configureBrowser: b => b with { UploadWaitSeconds = 1 });

        var outcome = await session.SendMessageAsync("request",
            [new Abstractions.Models.Snapshot.SnapshotAttachment("code-dump-result.md", Abstractions.Models.Snapshot.SnapshotAttachmentKind.CodeDump, "attachment content")]);

        outcome.IsSuccess.Should().BeTrue();
        page.LastInsertedText.Should().NotContain("--- attached:",
            "pasting on top of a successful SetInputFiles would double the content");
    }

    [Fact]
    public async Task StopIndicatorPolling_UsesInstantChecks_AtMost100MsApart()
    {
        // No reply ever completes: the session polls the stop indicator until the timeout.
        // With instant checks the poll interval is the 100 ms default — never 3 x 400 ms.
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () => page.SimulateUserMessageAccepted();
        var session = CreateSession(page, configureBrowser: b => b with { ReplyTimeoutSeconds = 1 });

        (await session.SendMessageAsync("request", [])).IsSuccess.Should().BeTrue();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await session.WaitForReplyAsync();
        sw.Stop();

        // A serialized 3x400ms fallback wait would yield at most ~2-3 polls per second; instant
        // checks with the 100ms default interval yield ~10 (loosened for parallel-CPU jitter —
        // wall-clock gaps drift above 100ms under load, but never near a serialized timeout).
        page.StopPolls.Should().BeGreaterThanOrEqualTo(5,
            "the stop-indicator polling must use instant checks, not serialized selector waits");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "instant checks must not serialize timeouts");
    }

    [Fact]
    public async Task CopyAttempt_TimedAndReasonLogged_WhenButtonNotMatched()
    {
        // Auto mode, NO copy button fallback matches: the reply falls back to the DOM, the
        // "copy" phase is still timed, and the reason travels in the turn-log event.
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.CopyButtonVisible = false; // WaitForMessageCopyButton finds nothing
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "dom reply", 2));
        };
        var recorder = new TimingRecorder();
        var session = CreateSession(page, timings: recorder);

        (await session.SendMessageAsync("request", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).IsSuccess.Should().BeTrue();

        recorder.Entries.Select(e => e.Phase).Should().Contain("copy",
            "the copy attempt is timed even when it ends in a Dom fallback");
    }

    [Fact]
    public async Task CopyButtonMissOnce_DisablesTheCopyPathForTheSession()
    {
        // Auto mode, NO copy button ever matches: the FIRST miss may cost the (capped) wait,
        // every LATER reply must skip the copy path entirely — one wait per session, not one
        // per message. The wait itself is capped at 3 s even with a 10 s reply timeout.
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.CopyButtonVisible = false;
        var turn = 0;
        page.OnSent = () =>
        {
            turn++;
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply($"44{turn + 1}", $"reply {turn}", turn + 1));
        };
        var session = CreateSession(page); // ReplyTimeoutSeconds = 10

        (await session.SendMessageAsync("request one", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).Data.Should().Be("reply 1");
        (await session.SendMessageAsync("request two", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).Data.Should().Be("reply 2");

        page.CopyButtonWaitCalls.Should().Be(1,
            "the first miss opens the circuit breaker; the second reply goes straight to the DOM");
        page.CopyButtonWaitTimeouts.Single().Should().BeLessOrEqualTo(TimeSpan.FromSeconds(3),
            "the copy-button wait is capped at 3 s regardless of the reply timeout");
        session.LastReplyOrigin.Should().Contain("copy disabled",
            "the second reply's origin says WHY the copy path was skipped");
    }

    [Fact]
    public async Task ReplySourceDom_NeverWaitsForTheCopyButton()
    {
        // Browser.ReplySource=Dom short-circuits the copy path: zero copy-button waits even
        // though the button is (pretend-)visible and would match.
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.CopyButtonVisible = true;
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "dom reply", 2));
        };
        var session = CreateSession(page, configureBrowser: b => b with { ReplySource = ReplySourceMode.Dom });

        (await session.SendMessageAsync("request", [])).IsSuccess.Should().BeTrue();
        (await session.WaitForReplyAsync()).Data.Should().Be("dom reply");

        page.CopyButtonWaitCalls.Should().Be(0, "Dom mode never touches the copy path");
    }

    private const string TruncatedReply = "@@newfile hello.html\n<<<CONTENT\n<h1>hi</h1>\nCONTENT>>>\n@@end";
    private const string CompleteReply = TruncatedReply + "\n@@eof";

    [Fact]
    public async Task TruncatedReply_IsRejected_ThenACompletedReplyIsAccepted()
    {
        // A minimized Chrome can throttle the stream: an AgentScript reply without @@eof is
        // never accepted — the wait keeps polling until the stream completes, then returns.
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        var events = new List<(string Type, object Payload)>();
        var firstTruncationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            page.SimulateAssistantReply("442", TruncatedReply, 2); // the throttled stream lands first
            _ = Task.Run(async () =>
            {
                await firstTruncationSeen.Task; // the wait loop rejected the truncated text
                await Task.Delay(100);          // then the stream completes
                page.SimulateAssistantReply("442", CompleteReply, 2);
            });
        };
        var session = CreateSession(page, logEvent: (type, payload) =>
        {
            if (type == "reply-truncated")
            {
                lock (events)
                    events.Add((type, payload));
                firstTruncationSeen.TrySetResult();
            }
            return Task.CompletedTask;
        });

        (await session.SendMessageAsync("create a hello.html", [])).IsSuccess.Should().BeTrue();
        var reply = await session.WaitForReplyAsync();

        reply.IsSuccess.Should().BeTrue("the stream resumes and the completed reply must be accepted");
        reply.Data.Should().Contain("@@newfile hello.html", "the AgentScript body survived extraction");
        reply.Data.TrimEnd().EndsWith("@@eof", StringComparison.OrdinalIgnoreCase).Should().BeTrue(
            "the COMPLETED reply is returned — never the truncated one (extraction collapses newlines)");
        page.FocusedTabs.Should().BeEmpty("one truncation never triggers the un-minimize recovery");
        lock (events)
        {
            var truncations = events.Where(e => e.Type == "reply-truncated").ToList();
            truncations.Should().NotBeEmpty("truncation events give the diagnostic trail");
        }
    }

    [Fact]
    public async Task FiveTruncations_FocusOnce_ThenFiveMore_FailTheWait()
    {
        // 5 truncated detections → ONE un-minimize recovery (FocusTabAsync); 5 MORE even with
        // the window visible → KA_REPLY_TRUNCATED failure. The truncated text is never returned.
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        var events = new List<(string Type, System.Text.Json.JsonElement Payload)>();
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            page.SimulateAssistantReply("442", TruncatedReply, 2); // the stream never completes
        };
        var session = CreateSession(page, logEvent: (type, payload) =>
        {
            lock (events)
                events.Add((type, System.Text.Json.JsonSerializer.SerializeToElement(payload)));
            return Task.CompletedTask;
        });

        (await session.SendMessageAsync("create a hello.html", [])).IsSuccess.Should().BeTrue();
        var reply = await session.WaitForReplyAsync();

        reply.IsFailure.Should().BeTrue("a permanently truncated stream must fail, not return garbage");
        reply.Error.Code.Should().Be("KA_REPLY_TRUNCATED");
        reply.Error.Message.Should().Contain("cut off repeatedly even with the window visible");
        page.FocusedTabs.Should().ContainSingle("exactly ONE un-minimize recovery, never routine");
        lock (events)
        {
            var truncations = events.Where(e => e.Type == "reply-truncated")
                .Select(e => e.Payload).ToList();
            truncations.Should().HaveCount(10, "5 detections before the recovery + 5 after");
            truncations[0].GetProperty("Attempt").GetInt32().Should().Be(1);
            truncations[0].GetProperty("FocusTriggered").GetBoolean().Should().BeFalse();
            truncations[5].GetProperty("Attempt").GetInt32().Should().Be(1,
                "the retry counter reset to zero after the recovery");
            truncations[5].GetProperty("FocusTriggered").GetBoolean().Should().BeTrue(
                "post-recovery events carry the focus-triggered flag");
            truncations[9].GetProperty("Attempt").GetInt32().Should().Be(5);
        }
    }

    [Fact]
    public async Task ChatLimitProbe_CostsOneRoundTripPerWait()
    {
        var page = new FakeChatPage { ChatLimitVisible = true };
        ArrangeFirstReplyDelivered(page);
        var session = CreateSession(page);

        (await session.WaitForReplyAsync()).IsFailure.Should().BeTrue();
        (await session.WaitForReplyAsync()).IsFailure.Should().BeTrue();

        page.ChatLimitCalls.Should().Be(2, "one instant probe per reply wait (2 waits), not one per selector");
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

        // primary attempt (click) + exactly one retry, which is ENTER (a repeated click on a
        // mis-identified control only opens dialogs)
        page.Sends.Select(s => s.Method).Should().Equal("click", "enter");
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

    // ---------- send-logic risk tests ----------

    [Fact]
    public async Task SendA_ConfirmedByClearedInputWithUnchangedCount_NoSecondSend()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () =>
        {
            // The message left the composer, but the virtualized list did not grow.
            page.InputHasText = false;
            page.StopVisible = true;
            // ItemCount intentionally unchanged; the new reply arrives after a delay.
            _ = Task.Delay(150).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "second reply", 2));
        };
        var session = CreateSession(page, sendConfirmSeconds: 1);

        var outcome = await session.SendMessageAsync("request", []);
        var reply = await session.WaitForReplyAsync();

        outcome.IsSuccess.Should().BeTrue("a cleared input means the message was sent");
        page.Sends.Should().ContainSingle("exactly one send attempt — no duplicate");
        page.Sends[0].Method.Should().Be("click");
        reply.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SendB_InputStillHoldsText_ExactlyOneRetry()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () => { }; // nothing lands: input keeps the text, no list change
        var session = CreateSession(page, sendConfirmSeconds: 1);

        var outcome = await session.SendMessageAsync("request", []);

        outcome.IsFailure.Should().BeTrue();
        // primary attempt (click) + exactly one retry, which is ENTER (a repeated click on a
        // mis-identified control only opens dialogs)
        page.Sends.Select(s => s.Method).Should().Equal("click", "enter");
    }

    [Fact]
    public async Task SendC_ClickImpossible_EnterPressedOnce()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.FailClick = true;
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "reply via enter", 2));
        };
        var session = CreateSession(page);

        var outcome = await session.SendMessageAsync("request", []);

        outcome.IsSuccess.Should().BeTrue();
        page.Sends.Select(s => s.Method).Should().Equal("click-failed", "enter");
        (await session.WaitForReplyAsync()).Data.Should().Be("reply via enter");
    }

    [Fact]
    public async Task SendD_UploadOkButChipsUndetectable_FileNameVisible_NoPaste()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.FailUpload = false;
        page.ChipsVisible = false; // chips never render
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "reply", 2));
        };
        var session = CreateSession(page, uploadWaitSeconds: 1);
        var attachment = new SnapshotAttachment("tree-view-result.txt", SnapshotAttachmentKind.TreeView, "tree");

        // The file input registers the upload right away, even though no chip renders.
        page.InputFileNames.Add("tree-view-result.txt");
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "reply", 2));
        };

        var outcome = await session.SendMessageAsync("request", [attachment]);

        outcome.IsSuccess.Should().BeTrue();
        page.LastInsertedText.Should().Be("request", "the content must not be pasted on top of a working upload");
    }

    [Fact]
    public async Task SendE_UploadFailed_ContentIsPasted()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.FailUpload = true;
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "reply", 2));
        };
        var session = CreateSession(page, uploadWaitSeconds: 1);
        var attachment = new SnapshotAttachment("code-dump-result.md", SnapshotAttachmentKind.CodeDump, "dump content");

        var outcome = await session.SendMessageAsync("request", [attachment]);

        outcome.IsSuccess.Should().BeTrue();
        page.LastInsertedText.Should().Contain("--- attached: code-dump-result.md ---");
        page.LastInsertedText.Should().Contain("dump content");
    }

    // ---------- diagnostics hint tests ----------

    [Fact]
    public async Task SendNotConfirmed_WithDebug_ShowsScreenshotPath()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () => { };
        var session = CreateSession(page, sendConfirmSeconds: 1);
        var options = new ChatSessionOptions
        {
            WorkspaceRoot = _root,
            Browser = new BrowserOptions
            {
                MinDelayMs = 0, MaxDelayMs = 0,
                SendConfirmSeconds = 1, UploadWaitSeconds = 1,
                ReplyTimeoutSeconds = 5, Debug = true,
            },
        };
        var debugSession = new PlaywrightChatSession(
            page, () => ValueTask.CompletedTask, options,
            new ReplyExtractor(), new RateLimiter(1000, 0, 0, new FakeDateTimeProvider()),
            new LoggerAdapter<PlaywrightChatSession>(NullLoggerFactory.Instance),
            ImanFileStorageFactory.CreateStorage(), pollDelayMs: 20);

        var outcome = await debugSession.SendMessageAsync("request", []);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Message.Should().Contain("Debug screenshot:");
        page.Screenshots.Should().NotBeEmpty();
        page.Screenshots[0].Should().Contain(_root);
        page.Screenshots[0].Should().Contain("send-not-confirmed");
    }

    [Fact]
    public async Task SendNotConfirmed_WithoutDebug_SuggestsKeylessDoctor()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () => { };
        var session = CreateSession(page, sendConfirmSeconds: 1);

        var outcome = await session.SendMessageAsync("request", []);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Message.Should().Contain("keyless doctor");
        outcome.Error.Message.Should().NotContain("Debug screenshot");
    }

    // ---------- upload readiness tests ----------

    [Fact]
    public async Task Upload_SendEnabledOnlyAfterPolls_SendWaitsForEnablement()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.ChipsVisible = true;
        page.SendEnabled = false;           // still uploading
        var enabledAt = DateTime.UtcNow;
        _ = Task.Delay(600).ContinueWith(_ =>
        {
            page.SendEnabled = true;        // upload finished
            enabledAt = DateTime.UtcNow;
        });
        DateTime? clickedAt = null;
        page.OnSent = () =>
        {
            clickedAt = DateTime.UtcNow;
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "reply", 2));
        };
        var session = CreateSession(page, uploadWaitSeconds: 5);
        var attachment = new SnapshotAttachment("code-dump-result.md", SnapshotAttachmentKind.CodeDump, "dump");

        var outcome = await session.SendMessageAsync("request", [attachment]);

        outcome.IsSuccess.Should().BeTrue();
        clickedAt.Should().NotBeNull();
        (clickedAt.Value - enabledAt).Should().BeGreaterThanOrEqualTo(TimeSpan.Zero,
            "the send click must not happen while the send control is still disabled");
    }

    [Fact]
    public async Task Upload_SendControlUndetectable_DoesNotDeadlock()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.ChipsVisible = true;
        page.SendEnabled = null;            // no send-control selector matches at all
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(100).ContinueWith(_ =>
                page.SimulateAssistantReply("442", "reply", 2));
        };
        var session = CreateSession(page, uploadWaitSeconds: 2);
        var attachment = new SnapshotAttachment("tree-view-result.txt", SnapshotAttachmentKind.TreeView, "tree");
        var startedAt = DateTime.UtcNow;

        var outcome = await session.SendMessageAsync("request", [attachment]);

        outcome.IsSuccess.Should().BeTrue();
        (DateTime.UtcNow - startedAt).Should().BeLessThan(TimeSpan.FromSeconds(2),
            "an undetectable send control must not block until the upload timeout");
        (await session.WaitForReplyAsync()).Data.Should().Be("reply");
    }

    // ---------- stale-composer reset + offline detection ----------

    private static SnapshotAttachment Attachment(string name, string content)
        => new(name, SnapshotAttachmentKind.CodeDump, content);

    [Fact]
    public async Task Reset_RemovesStaleTextAndChips_ThenSendSucceedsInSameCall()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.InputHasText = true;
        page.StaleChipCount = 2;
        page.FailedChipCount = 1;
        page.ChipsVisible = true;
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(50).ContinueWith(_ => page.SimulateAssistantReply("442", "ok", 2));
        };
        var session = CreateSession(page);

        var outcome = await session.SendMessageAsync("fresh message", []);

        outcome.IsSuccess.Should().BeTrue();
        page.ResetCalls.Should().Be(1);
        page.LastResetMethod.Should().Be("chips removed");
        page.LastInsertedText.Should().Be("fresh message");
    }

    [Fact]
    public async Task Reset_RemoveClickDoesNothing_ReloadFallbackCleans()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.InputHasText = true;
        page.StaleChipCount = 1;
        page.RemoveClickDoesNothing = true;
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(50).ContinueWith(_ => page.SimulateAssistantReply("442", "ok", 2));
        };
        var session = CreateSession(page);

        var outcome = await session.SendMessageAsync("message", []);

        outcome.IsSuccess.Should().BeTrue();
        page.ReloadCalls.Should().Be(1, "the reload fallback is used when the remove clicks do nothing");
        page.LastResetMethod.Should().Be("page reloaded");
    }

    [Fact]
    public async Task Reset_ReloadAlsoFails_ComposerNotClean_NothingSent()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.InputHasText = true;
        page.StaleChipCount = 1;
        page.RemoveClickDoesNothing = true;
        page.FailReload = true;
        var session = CreateSession(page);

        var outcome = await session.SendMessageAsync("message", []);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.ComposerNotClean);
        page.Sends.Should().BeEmpty("nothing is sent from a dirty composer");
        page.UploadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Offline_BeforeSend_FailsFast_ZeroUploadsZeroPastes()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.Online = false;
        var session = CreateSession(page);

        var outcome = await session.SendMessageAsync("message", [Attachment("one.md", "content")]);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.NetworkOffline);
        outcome.Error.Message.Should().Contain("Nothing was sent");
        page.UploadCalls.Should().Be(0, "nothing is uploaded while offline");
        page.LastInsertedText.Should().BeNull("nothing is pasted while offline");
    }

    [Fact]
    public async Task ConnectionDropsDuringUploadWait_NetworkOffline_ComposerCleaned()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnlineDropsAfterFirstCheck = true;
        page.OnUpload = () =>
        {
            page.StaleChipCount = 1;
            page.ChipsVisible = true;
        };
        var session = CreateSession(page, uploadWaitSeconds: 5);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await session.SendMessageAsync("message", [Attachment("one.md", "content")]);
        sw.Stop();

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.NetworkOffline);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the offline check stops the upload wait at once");
        page.ResetCalls.Should().Be(2, "the initial reset plus the best-effort cleanup");
        page.StaleChipCount.Should().Be(0, "the composer is cleaned after the failure");
    }

    [Fact]
    public async Task Message1FailsOffline_Message2OnSameSession_SucceedsWithItsOwnAttachments()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.Online = false;
        page.StaleChipCount = 2;
        page.FailedChipCount = 1;
        page.ChipsVisible = true;
        var session = CreateSession(page, uploadWaitSeconds: 2);

        var outcome1 = await session.SendMessageAsync("first message", [Attachment("one.md", "one")]);

        outcome1.Error.Code.Should().Be(ErrorCodes.NetworkOffline);
        page.UploadCalls.Should().Be(0, "the offline gate runs before any upload");

        page.Online = true;
        page.InputFileNames.Add("a.md");
        page.OnSent = () =>
        {
            page.SimulateUserMessageAccepted();
            _ = Task.Delay(50).ContinueWith(_ => page.SimulateAssistantReply("443", "second reply", 2));
        };

        var outcome2 = await session.SendMessageAsync("second message",
            [Attachment("a.md", "A"), Attachment("b.md", "B")]);

        outcome2.IsSuccess.Should().BeTrue();
        page.ResetCalls.Should().Be(2, "message 2's reset cleaned the stale chips left by message 1");
        page.StaleChipCount.Should().Be(0);
        page.FailedChipCount.Should().Be(0);
        page.UploadCalls.Should().Be(1);
        page.LastInsertedText.Should().Be("second message", "only message 2's text, no stale content");
    }

    [Fact]
    public async Task EnterRetry_DoesNotTriggerAnotherComposerReset()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnSent = () => { }; // nothing lands: unconfirmed → exactly one Enter retry
        var session = CreateSession(page, sendConfirmSeconds: 1);

        var outcome = await session.SendMessageAsync("request", []);

        outcome.IsFailure.Should().BeTrue();
        page.Sends.Select(s => s.Method).Should().Equal("click", "enter");
        page.ResetCalls.Should().Be(1, "the reset runs once per message — the Enter retry must not wipe the pending text");
    }

    [Fact]
    public async Task CleanupComposer_NeverThrows_WhenThePageIsClosed()
    {
        var page = new FakeChatPage();
        ArrangeFirstReplyDelivered(page);
        page.OnUpload = () =>
        {
            page.Closed = true;
            page.StaleChipCount = 1;
            page.ChipsVisible = true;
        };
        var session = CreateSession(page, uploadWaitSeconds: 5);

        var act = async () => await session.SendMessageAsync("message", [Attachment("one.md", "content")]);

        var outcome = await act.Should().NotThrowAsync();
        outcome.Subject.IsFailure.Should().BeTrue("the original failure is reported, not the cleanup failure");
        outcome.Subject.Error.Message.Should().Contain("The page is closed.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
