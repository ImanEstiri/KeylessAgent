using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Logging;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// The doctor report builder over an <see cref="IChatPageInspector"/>: classifies every
/// selectors.json key as Matched / NotFound / NotApplicableNow, reports login, toggle and
/// input state, and writes trimmed snapshots when requested. Never sends a message and never
/// changes the chat.
/// </summary>
internal sealed class ChatDiagnosticsService : IChatDiagnostics
{
    private readonly Func<ImanSoftware.KeylessAgent.Abstractions.Models.Chat.ChatSessionOptions, IAgentUserInterface, CancellationToken, Task<Outcome<(IChatPageInspector Inspector, Func<ValueTask> Dispose)>>> _openPage;

    public ChatDiagnosticsService()
        : this((session, ui, cancellationToken) => OpenRealPageAsync(session, ui, cancellationToken))
    {
    }

    internal ChatDiagnosticsService(
        Func<ImanSoftware.KeylessAgent.Abstractions.Models.Chat.ChatSessionOptions, IAgentUserInterface, CancellationToken, Task<Outcome<(IChatPageInspector, Func<ValueTask>)>>> openPage)
    {
        _openPage = openPage;
    }

    /// <summary>
    /// The keys that must match for the doctor to exit 0. Conditional keys (Stop, chips,
    /// Reasoning, FinalAnswer without messages, LoginIndicator) are never required.
    /// </summary>
    internal static readonly IReadOnlyList<string> RequiredKeys =
    [
        "InputBox", "SendButton", "FileInput", "NewChatButton",
        "MessageItems", "AssistantMessage", "DeepThinkToggle", "SearchToggle",
    ];

    /// <summary>Keys that legitimately cannot exist in some page states.</summary>
    internal static readonly IReadOnlyList<string> ConditionalKeys =
    [
        "StopIndicator", "AttachmentChips", "Reasoning", "FinalAnswer", "LoginIndicator",
    ];

    /// <inheritdoc />
    public async Task<Outcome<ChatDiagnosticsReport>> InspectAsync(
        string workspaceRoot,
        ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.BrowserOptions browser,
        string? captureDirectory,
        bool watch,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var selectors = await SelectorsCatalog.LoadAsync(workspaceRoot, browser.SelectorsFile, cancellationToken);
            if (selectors.IsFailure)
                return Outcome.Failure<ChatDiagnosticsReport>(selectors.Error);

            var opened = await _openPage(new ImanSoftware.KeylessAgent.Abstractions.Models.Chat.ChatSessionOptions
            {
                Browser = browser,
                WorkspaceRoot = workspaceRoot,
            }, new DiagnosticsUi(), cancellationToken);
            if (opened.IsFailure)
                return Outcome.Failure<ChatDiagnosticsReport>(opened.Error);

            var (inspector, dispose) = opened.Data;
            try
            {
                return await BuildReportAsync(inspector, selectors.Data, captureDirectory, watch, cancellationToken);
            }
            finally
            {
                await dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Outcome.Failure<ChatDiagnosticsReport>(new OutcomeError(ex));
        }
    }

    private static async Task<Outcome<(IChatPageInspector, Func<ValueTask>)>> OpenRealPageAsync(
        ImanSoftware.KeylessAgent.Abstractions.Models.Chat.ChatSessionOptions session,
        IAgentUserInterface ui,
        CancellationToken cancellationToken)
    {
        var connector = new BrowserConnector();
        var connection = await connector.ConnectAsync(session.Browser, ui, cancellationToken);
        if (connection.IsFailure)
            return Outcome.Failure<(IChatPageInspector, Func<ValueTask>)>(connection.Error);

        var (attachment, rawPage) = connection.Data;
        var page = (Microsoft.Playwright.IPage)rawPage;

        var selectors = await SelectorsCatalog.LoadAsync(session.WorkspaceRoot, session.Browser.SelectorsFile, cancellationToken);
        if (selectors.IsFailure)
            return Outcome.Failure<(IChatPageInspector, Func<ValueTask>)>(selectors.Error);

        var provider = new PlaywrightChatProvider();
        await provider.WaitForLoginIfNeeded(page, selectors.Data, session.Browser, cancellationToken);

        IChatPageInspector inspector = new PlaywrightChatInspector(page);
        return Outcome.Successful((inspector, (Func<ValueTask>)(() => DisposePageAsync(page))));
    }

    /// <summary>Doctor closes only the tab it opened — the Chrome window stays.</summary>
    private static async ValueTask DisposePageAsync(Microsoft.Playwright.IPage page)
    {
        try
        {
            await page.CloseAsync();
        }
        catch
        {
            // Closing must never crash diagnostics.
        }
    }

    private static async ValueTask DisposeAsync(Microsoft.Playwright.IBrowser? browser, bool ownsBrowser)
    {
        try
        {
            if (ownsBrowser && browser is not null)
            {
                var contexts = browser.Contexts;
                if (contexts is { Count: > 0 })
                    await contexts[0].CloseAsync();
            }
            else if (browser is not null)
            {
                await browser.CloseAsync();
            }
        }
        catch
        {
            // Closing must never crash diagnostics.
        }
    }

    internal static async Task<ChatDiagnosticsReport> BuildReportAsync(
        IChatPageInspector inspector,
        SelectorsCatalog catalog,
        string? captureDirectory,
        bool watch,
        CancellationToken cancellationToken)
    {
        var captured = new List<string>();
        var login = await inspector.ProbeAsync(catalog.LoginIndicator, cancellationToken);
        var loggedIn = login is null;

        var checks = new List<SelectorCheck>();
        foreach (var (key, selectors) in SelectorEntries(catalog))
        {
            checks.Add(await ClassifyAsync(key, selectors, inspector, login, cancellationToken));
        }

        var deepThink = await inspector.GetToggleStateAsync(catalog.DeepThinkToggle, cancellationToken);
        var search = await inspector.GetToggleStateAsync(catalog.SearchToggle, cancellationToken);
        var inputEditable = await inspector.IsInputEditableAsync(cancellationToken);

        if (captureDirectory is not null)
        {
            captured.AddRange(await CaptureAsync(inspector, catalog, captureDirectory, cancellationToken));
        }

        if (watch)
        {
            captured.AddRange(await CaptureWatchAsync(inspector, catalog, captureDirectory ?? DefaultCaptureDir(), cancellationToken));
        }

        var allRequired = checks
            .Where(c => RequiredKeys.Contains(c.Key))
            .All(c => c.Status == SelectorCheckStatus.Matched);

        return new ChatDiagnosticsReport(
            inspector.Url,
            loggedIn,
            deepThink,
            search,
            inputEditable,
            checks,
            allRequired,
            captured);
    }

    internal static IEnumerable<(string Key, string[] Selectors)> SelectorEntries(SelectorsCatalog catalog)
    {
        yield return ("InputBox", catalog.InputBox);
        yield return ("SendButton", catalog.SendButton);
        yield return ("FileInput", catalog.FileInput);
        yield return ("NewChatButton", catalog.NewChatButton);
        yield return ("MessageItems", catalog.MessageItems);
        yield return ("AssistantMessage", catalog.AssistantMessage);
        yield return ("FinalAnswer", catalog.FinalAnswer);
        yield return ("Reasoning", catalog.Reasoning);
        yield return ("StopIndicator", catalog.StopIndicator);
        yield return ("DeepThinkToggle", catalog.DeepThinkToggle);
        yield return ("SearchToggle", catalog.SearchToggle);
        yield return ("AttachmentChips", catalog.AttachmentChips);
        yield return ("LoginIndicator", catalog.LoginIndicator);
    }

    internal static async Task<SelectorCheck> ClassifyAsync(
        string key, string[] selectors, IChatPageInspector inspector,
        (int Index, int Count)? login, CancellationToken cancellationToken)
    {
        if (key == "LoginIndicator")
        {
            return login is not null
                ? new SelectorCheck(key, SelectorCheckStatus.Matched, login.Value.Index, login.Value.Count,
                    "Login page detected: log in manually; doctor does not proceed past this state.")
                : new SelectorCheck(key, SelectorCheckStatus.NotApplicableNow, -1, 0,
                    "Not on the login page (the session looks logged in).");
        }

        var probe = await inspector.ProbeAsync(selectors, cancellationToken);
        if (probe is not null)
        {
            var detail = key switch
            {
                "FinalAnswer" => "Final-answer element exists (a reply is present or in progress).",
                "Reasoning" => "A reasoning block exists (a thinking message is present).",
                "StopIndicator" => "Generating is in progress.",
                "AttachmentChips" => "Attachment chips are visible (files attached).",
                _ => "Selector matched.",
            };
            return new SelectorCheck(key, SelectorCheckStatus.Matched, probe.Value.Index, probe.Value.Count, detail);
        }

        // Nothing matched: is that legitimate for the current state?
        var notApplicable = key switch
        {
            "StopIndicator" => "Nothing is generating right now.",
            "AttachmentChips" => "Nothing is attached right now.",
            "Reasoning" => "No message with a reasoning block is visible right now.",
            "FinalAnswer" => "No assistant message is visible right now (new chat).",
            "LoginIndicator" => "Not on the login page.",
            _ => string.Empty,
        };

        return notApplicable.Length > 0
            ? new SelectorCheck(key, SelectorCheckStatus.NotApplicableNow, -1, 0, notApplicable)
            : new SelectorCheck(key, SelectorCheckStatus.NotFound, -1, 0, "No fallback selector matched — this key is required and must be fixed.");
    }

    internal static async Task<IReadOnlyList<string>> CaptureAsync(
        IChatPageInspector inspector, SelectorsCatalog catalog, string directory, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        Directory.CreateDirectory(directory);

        async Task SaveAsync(string name, string? html)
        {
            if (string.IsNullOrEmpty(html))
                return;
            var path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, html, cancellationToken);
            files.Add(path);
        }

        await SaveAsync("composer.html", await inspector.CaptureTrimmedAsync(catalog.InputBox, cancellationToken));
        await SaveAsync("newest-message.html", await inspector.CaptureTrimmedAsync(catalog.AssistantMessage, cancellationToken));
        await SaveAsync("sidebar.html", await inspector.CaptureTrimmedAsync(catalog.NewChatButton, cancellationToken));
        await SaveAsync("attachment-chips.html", await inspector.CaptureTrimmedAsync(catalog.AttachmentChips, cancellationToken));
        return files;
    }

    /// <summary>
    /// --watch: waits for the user to send a prompt manually, then captures the generating
    /// state (stop indicator) and the finished state once each.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> CaptureWatchAsync(
        IChatPageInspector inspector,
        SelectorsCatalog catalog,
        string directory,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        Directory.CreateDirectory(directory);

        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generating = await inspector.ProbeAsync(catalog.StopIndicator, cancellationToken);
            if (generating is not null)
                break;
            await Task.Delay(500, cancellationToken);
        }

        var stopHtml = await inspector.CaptureTrimmedAsync(catalog.StopIndicator, cancellationToken);
        if (!string.IsNullOrEmpty(stopHtml))
        {
            var path = Path.Combine(directory, "watch-generating-stop.html");
            await File.WriteAllTextAsync(path, stopHtml, cancellationToken);
            files.Add(path);
        }

        var newestDuringGenerating = await inspector.CaptureTrimmedAsync(catalog.AssistantMessage, cancellationToken);
        if (!string.IsNullOrEmpty(newestDuringGenerating))
        {
            var path = Path.Combine(directory, "watch-generating-message.html");
            await File.WriteAllTextAsync(path, newestDuringGenerating, cancellationToken);
            files.Add(path);
        }

        // Wait for the finished state: stop indicator gone.
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await inspector.ProbeAsync(catalog.StopIndicator, cancellationToken) is null)
                break;
            await Task.Delay(500, cancellationToken);
        }

        var finished = await inspector.CaptureTrimmedAsync(catalog.AssistantMessage, cancellationToken);
        if (!string.IsNullOrEmpty(finished))
        {
            var path = Path.Combine(directory, "watch-finished-message.html");
            await File.WriteAllTextAsync(path, finished, cancellationToken);
            files.Add(path);
        }

        return files;
    }

    private static string DefaultCaptureDir()
        => Path.Combine(Path.GetTempPath(), "keylessagent-capture");

    /// <summary>
    /// The connector reports its decisions; doctor surfaces them as progress lines via the
    /// same channel the agent loop uses.
    /// </summary>
    private sealed class DiagnosticsUi : IAgentUserInterface
    {
        public void Say(string message)
        {
        }

        public void ShowProgress(AgentProgressEvent progressEvent)
            => Console.WriteLine($"[{progressEvent.Type}] {progressEvent.Detail}");

        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(true);

        public Task<string> AskAsync(string question) => Task.FromResult(string.Empty);

        public void ShowDiff(FileChange change)
        {
        }

        public IDisposable BeginWait(string message) => new NopScope();

        private sealed class NopScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
