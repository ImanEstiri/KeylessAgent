namespace ImanSoftware.KeylessAgent.Tests.Fakes;

/// <summary>
/// A scriptable chat page for the session tests: the test mutates state (directly or from a
/// background task) to simulate the virtualized DOM evolving over time.
/// </summary>
internal sealed class FakeChatPage : ImanSoftware.KeylessAgent.Core.Implementation.Browser.IChatPage
{
    private readonly object _lock = new();

    public string Url { get; set; } = "https://chat.deepseek.com/a/fake";

    /// <summary>The newest assistant message's virtual-list key (null = not present).</summary>
    public string? NewestAssistantKey { get; set; }

    /// <summary>The newest assistant message's final-answer text (null = no final answer yet).</summary>
    public string? NewestAssistantText { get; set; }

    public int AssistantVisibleCount { get; set; }

    public int ItemCount { get; set; }

    public bool StopVisible { get; set; }

    public bool InputHasText { get; set; }

    /// <summary>Send control state: true/false, or null when no send control exists.</summary>
    public bool? SendEnabled { get; set; } = true;

    public bool ChipsVisible { get; set; }

    /// <summary>When true, TryUploadFiles pretends the file input cannot be used.</summary>
    public bool FailUpload { get; set; }

    /// <summary>When true, TryClickSend cannot find the button (returns false, no send).</summary>
    public bool FailClick { get; set; }

    /// <summary>File names the hidden file input reports.</summary>
    public List<string> InputFileNames { get; } = new();

    /// <summary>File names visible in composer-near chip elements.</summary>
    public HashSet<string> VisibleFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last text handed to InsertText (paste detection in tests).</summary>
    public string? LastInsertedText { get; private set; }

    /// <summary>When set, screenshots are recorded here instead of the filesystem.</summary>
    public List<string> Screenshots { get; } = new();

    /// <summary>Records ("click" | "click-failed" | "enter") in order, plus whether chips were visible at send time.</summary>
    public List<(string Method, bool ChipsVisible)> Sends { get; } = new();

    /// <summary>Runs after a successful send; the test uses it to evolve the fake DOM.</summary>
    public Action? OnSent { get; set; }

    public Task<ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp?> GetNewestAssistantStampAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (NewestAssistantKey is null && NewestAssistantText is null)
                return Task.FromResult<ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp?>(null);

            return Task.FromResult<ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp?>(
                new ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp(
                    NewestAssistantKey,
                    TextHash: StableHash(NewestAssistantText ?? string.Empty),
                    VisibleCount: AssistantVisibleCount));
        }
    }

    public Task<string?> GetNewestAssistantHtmlAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (NewestAssistantText is null)
                return Task.FromResult<string?>(null);

            // The real DOM escapes < and >; the reply extractor relies on that (entities are
            // decoded, raw < would be parsed as tags).
            var encoded = System.Net.WebUtility.HtmlEncode(NewestAssistantText);
            return Task.FromResult<string?>(
                $"<div class='ds-message'><div class='ds-markdown ds-assistant-message-main-content'>{encoded}</div></div>");
        }
    }

    public Task<int> GetMessageItemCountAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(ItemCount);
        }
    }

    /// <summary>How often the stop indicator was polled (round-trip counter).</summary>
    public int StopPolls { get; private set; }

    public Task<bool> IsStopIndicatorVisibleAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            StopPolls++;
            return Task.FromResult(StopVisible);
        }
    }

    /// <summary>How often the chat-limit notice was probed (round-trip counter).</summary>
    public int ChatLimitCalls { get; private set; }

    public Task<bool> IsInputClearedAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(!InputHasText);

    public Task<bool?> IsSendEnabledAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(SendEnabled);
        }
    }

    public Task<bool> AreAttachmentChipsVisibleAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(ChipsVisible);
        }
    }

    public Task<IReadOnlyList<string>> GetInputFileNamesAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>(InputFileNames.ToList());
        }
    }

    public Task<bool> IsFileNameVisibleAsync(string fileName, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(VisibleFileNames.Contains(fileName));
        }
    }

    public Task InsertTextAsync(string text, CancellationToken cancellationToken = default)
    {
        InputHasText = true;
        LastInsertedText = text;
        _lastSentText = text;
        return Task.CompletedTask;
    }

    public Task<bool> TryClickSendAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (FailClick)
            {
                Sends.Add(("click-failed", ChipsVisible));
                return Task.FromResult(false);
            }

            Sends.Add(("click", ChipsVisible));
            if (SendEnabled == false)
                return Task.FromResult(false);
        }

        OnSent?.Invoke();
        return Task.FromResult(true);
    }

    public Task PressEnterAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            Sends.Add(("enter", ChipsVisible));
        }

        OnSent?.Invoke();
        return Task.CompletedTask;
    }

    public Task<bool> TryUploadFilesAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        UploadCalls++;
        OnUpload?.Invoke();
        return Task.FromResult(!FailUpload);
    }

    /// <summary>Runs when SetInputFiles is about to be simulated (the upload is starting).</summary>
    public Action? OnUpload { get; set; }

    /// <summary>How often SetInputFiles was simulated.</summary>
    public int UploadCalls { get; private set; }

    // ---------- composer state / reset / online ----------

    /// <summary>Stale (non-failed) attachment chips currently in the composer.</summary>
    public int StaleChipCount { get; set; }

    /// <summary>Failed attachment chips currently in the composer.</summary>
    public int FailedChipCount { get; set; }

    /// <summary>When true, clicking a chip's remove control does nothing (the chip stays).</summary>
    public bool RemoveClickDoesNothing { get; set; }

    /// <summary>When true, the reload fallback fails (the composer stays dirty).</summary>
    public bool FailReload { get; set; }

    /// <summary>When true, every page call throws (simulates a closed page; cleanup must swallow).</summary>
    public bool Closed { get; set; }

    /// <summary>Whether the fake is online (navigator.onLine + origin probe).</summary>
    public bool Online { get; set; } = true;

    /// <summary>When true, the SECOND and later online checks report offline (a mid-flight drop).</summary>
    public bool OnlineDropsAfterFirstCheck { get; set; }

    /// <summary>How often the online state was checked.</summary>
    public int OnlineChecks { get; private set; }

    /// <summary>How often the composer reset ran.</summary>
    public int ResetCalls { get; private set; }

    /// <summary>How often the reload fallback ran.</summary>
    public int ReloadCalls { get; private set; }

    /// <summary>The Method of the last reset result ("already clean", "chips removed", "page reloaded", …).</summary>
    public string? LastResetMethod { get; private set; }

    private void ThrowIfClosed()
    {
        if (Closed)
            throw new InvalidOperationException("The page is closed.");
    }

    public Task<ImanSoftware.KeylessAgent.Core.Implementation.Browser.ComposerState> InspectComposerAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        lock (_lock)
        {
            return Task.FromResult(new ImanSoftware.KeylessAgent.Core.Implementation.Browser.ComposerState(
                TextLength: InputHasText ? 12 : 0,
                ChipCount: StaleChipCount,
                FailedChipCount: FailedChipCount,
                SendEnabled: SendEnabled));
        }
    }

    public async Task<ImanSoftware.KeylessAgent.Core.Implementation.Browser.ComposerResetResult> ResetComposerAsync(int replyTimeoutSeconds, CancellationToken cancellationToken = default)
    {
        ResetCalls++;
        ThrowIfClosed();

        // The real reset waits out a still-running generation first.
        var deadline = DateTime.UtcNow.AddSeconds(replyTimeoutSeconds);
        while (StopVisible && DateTime.UtcNow < deadline)
            await Task.Delay(10, cancellationToken);

        var method = "already clean";
        if (InputHasText)
        {
            InputHasText = false;
            method = "text cleared";
        }

        var chipsRemoved = 0;
        if (StaleChipCount > 0 || FailedChipCount > 0)
        {
            if (!RemoveClickDoesNothing)
            {
                chipsRemoved = StaleChipCount + FailedChipCount;
                StaleChipCount = 0;
                FailedChipCount = 0;
                ChipsVisible = false;
                method = "chips removed";
            }

            // RemoveClickDoesNothing: the chips stay — the reload fallback decides below.
        }

        if (InputHasText || StaleChipCount > 0 || FailedChipCount > 0)
        {
            ReloadCalls++;
            if (FailReload)
                return new ImanSoftware.KeylessAgent.Core.Implementation.Browser.ComposerResetResult(false, "not clean", chipsRemoved);

            method = "page reloaded";
            InputHasText = false;
            StaleChipCount = 0;
            FailedChipCount = 0;
            ChipsVisible = false;
        }

        LastResetMethod = method;
        return new ImanSoftware.KeylessAgent.Core.Implementation.Browser.ComposerResetResult(true, method, chipsRemoved);
    }

    public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default)
    {
        OnlineChecks++;
        var online = Online;
        if (OnlineDropsAfterFirstCheck && OnlineChecks >= 2)
            online = false;
        return Task.FromResult(online);
    }

    public Task<bool> AreFailedChipsVisibleAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        lock (_lock)
        {
            return Task.FromResult(FailedChipCount > 0);
        }
    }

    public Task ReloadAndWaitForComposerAsync(CancellationToken cancellationToken = default)
    {
        ReloadCalls++;
        ThrowIfClosed();
        if (!FailReload)
        {
            InputHasText = false;
            StaleChipCount = 0;
            FailedChipCount = 0;
            ChipsVisible = false;
            InputFileNames.Clear();
            VisibleFileNames.Clear();
            SendEnabled = true; // a freshly loaded composer has an enabled send control
        }

        return Task.CompletedTask;
    }

    public Task SaveDebugScreenshotAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        Screenshots.Add(fullPath);
        return Task.CompletedTask;
    }

    /// <summary>The rendered reasoning text of the newest assistant message.</summary>
    public string? ReasoningText { get; set; }

    /// <summary>Whether the message-level Copy control is present (hover simulated).</summary>
    public bool CopyButtonVisible { get; set; }

    /// <summary>What TryCaptureCopiedReplyAsync returns; null = both strategies fail.</summary>
    public string? CapturedReply { get; set; }

    /// <summary>Which capture strategy last "worked": interception / clipboard / none.</summary>
    public string LastCaptureStrategy { get; set; } = "none";

    /// <summary>The OS clipboard text the fake tracks (to assert it is untouched/restored).</summary>
    public string OsClipboard { get; set; } = "user-clipboard-content";

    /// <summary>Whether the OS clipboard is untouched (fake always leaves it alone).</summary>
    public bool OsClipboardUntouched => OsClipboard == "user-clipboard-content";

    public bool InterceptionPrepared { get; set; }

    public string? LastCopyClickedKey { get; set; }

    /// <summary>aria-pressed states by selector; a missing selector matches nothing (null).</summary>
    public Dictionary<string, string?> ToggleStates { get; } = new();

    /// <summary>Selectors whose toggle was actually clicked, in order.</summary>
    public List<string> ClickedToggles { get; } = new();

    public Task<string?> GetToggleStateAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        foreach (var selector in selectors)
        {
            if (ToggleStates.TryGetValue(selector, out var state))
                return Task.FromResult(state);
        }

        return Task.FromResult<string?>(null);
    }

    public Task<bool> ClickToggleAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        foreach (var selector in selectors)
        {
            if (ToggleStates.ContainsKey(selector))
            {
                ClickedToggles.Add(selector);
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<string?> GetNewestReasoningTextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ReasoningText);

    public Task<bool> WaitForMessageCopyButtonAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromResult(CopyButtonVisible);

    public Task PrepareReplyCaptureAsync(CancellationToken cancellationToken = default)
    {
        InterceptionPrepared = true;
        return Task.CompletedTask;
    }

    /// <summary>When true, the fake pretends interception produced nothing (clipboard path).</summary>
    public bool ForceClipboardStrategy { get; set; }

    /// <summary>Whether the chat-length-limit notice is visible (the chat is exhausted).</summary>
    public bool ChatLimitVisible { get; set; }

    public Task<bool> IsChatLimitNoticeVisibleAsync(CancellationToken cancellationToken = default)
    {
        ChatLimitCalls++;
        return Task.FromResult(ChatLimitVisible);
    }

    /// <summary>Round-trip counter for the instant any-visible check.</summary>
    public int IsAnyVisibleCalls { get; private set; }

    /// <summary>Scripted answers for IsAnyVisibleAsync, in order; defaults to true.</summary>
    public Queue<bool> AnyVisibleResults { get; } = new();

    /// <summary>Which selector list last hit the instant check (by first selector).</summary>
    public string? LastAnyVisibleFirstSelector { get; private set; }

    public Task<bool> IsAnyVisibleAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        IsAnyVisibleCalls++;
        LastAnyVisibleFirstSelector = selectors.FirstOrDefault();
        return Task.FromResult(AnyVisibleResults.Count > 0 ? AnyVisibleResults.Dequeue() : true);
    }

    /// <summary>Round-trip counter for the single parallel wait.</summary>
    public int WaitForAnyVisibleCalls { get; private set; }

    /// <summary>Scripted results for WaitForAnyVisibleAsync (matched fallback index or null), in order.</summary>
    public Queue<int?> AnyVisibleWaitResults { get; } = new();

    /// <summary>
    /// DOM-order visibility per selector for WaitForAnyVisibleAsync. When any entry is set,
    /// the fake models a real page: it returns the index of the FIRST VISIBLE selector in
    /// fallback order — a hidden earlier match never hides a later visible one. Missing
    /// selectors count as hidden.
    /// </summary>
    public Dictionary<string, bool> DomVisibility { get; } = new();

    public Task<int?> WaitForAnyVisibleAsync(string[] selectors, int timeoutMs, CancellationToken cancellationToken = default)
    {
        WaitForAnyVisibleCalls++;

        if (DomVisibility.Count > 0)
        {
            for (var index = 0; index < selectors.Length; index++)
            {
                if (DomVisibility.TryGetValue(selectors[index], out var visible) && visible)
                    return Task.FromResult<int?>(index);
            }

            return Task.FromResult<int?>(null);
        }

        return Task.FromResult(AnyVisibleWaitResults.Count > 0 ? AnyVisibleWaitResults.Dequeue() : 0);
    }

    /// <summary>How often the fake tab was focused (login-required flow).</summary>
    public List<string> FocusedTabs { get; } = new();

    public Task FocusTabAsync(CancellationToken cancellationToken = default)
    {
        FocusedTabs.Add(Url);
        return Task.CompletedTask;
    }

    public Task<string?> TryCaptureCopiedReplyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (CapturedReply is null)
        {
            LastCaptureStrategy = "none";
            return Task.FromResult<string?>(null);
        }

        // The real page prefers interception and falls back to the OS clipboard; the fake
        // reports which strategy the test configured.
        LastCaptureStrategy = InterceptionPrepared && !ForceClipboardStrategy ? "interception" : "clipboard";
        LastCopyClickedKey = NewestAssistantKey;
        return Task.FromResult<string?>(CapturedReply);
    }

    /// <summary>Simulates a successful send: the user message lands and generating starts.</summary>
    public void SimulateUserMessageAccepted()
    {
        lock (_lock)
        {
            InputHasText = false;
            ItemCount++;
            StopVisible = true;
            NewestItemKey = $"user-{ItemCount}";
            NewestItemIsUser = true;
        }
    }

    /// <summary>The item key of the newest message item (user or assistant).</summary>
    public string? NewestItemKey { get; set; }

    /// <summary>Whether the newest message item is the USER's (vs the assistant's reply).</summary>
    public bool NewestItemIsUser { get; set; }

    public Task<ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp?> GetNewestItemStampAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (NewestItemKey is null)
                return Task.FromResult<ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp?>(null);

            return Task.FromResult<ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp?>(new ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp(
                NewestItemKey,
                TextHash: 0,
                VisibleCount: ItemCount));
        }
    }

    public Task<string?> GetNewestItemTextAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(NewestItemIsUser ? _lastSentText : NewestAssistantText);
        }
    }

    private string? _lastSentText;

    /// <summary>Simulates the finished reply: stop indicator gone, new assistant message present.</summary>
    public void SimulateAssistantReply(string key, string text, int visibleCount)
    {
        lock (_lock)
        {
            StopVisible = false;
            NewestAssistantKey = key;
            NewestAssistantText = text;
            NewestItemKey = key;
            NewestItemIsUser = false;
            AssistantVisibleCount = visibleCount;
            ItemCount++;
        }
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in text)
                hash = (hash * 31) + c;
            return hash;
        }
    }
}
