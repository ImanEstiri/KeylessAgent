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

    public bool SendEnabled { get; set; } = true;

    public bool ChipsVisible { get; set; }

    /// <summary>When true, TryUploadFiles pretends the file input cannot be used.</summary>
    public bool FailUpload { get; set; }

    /// <summary>Records ("click" | "enter") in order, plus whether chips were visible at send time.</summary>
    public List<(string Method, bool ChipsVisible)> Sends { get; } = [];

    /// <summary>Runs after a successful send; the test uses it to evolve the fake DOM.</summary>
    public Action? OnSent { get; set; }

    public ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp? GetNewestAssistantStamp()
    {
        lock (_lock)
        {
            if (NewestAssistantKey is null && NewestAssistantText is null)
                return null;

            return new ImanSoftware.KeylessAgent.Core.Implementation.Browser.AssistantStamp(
                NewestAssistantKey,
                TextHash: StableHash(NewestAssistantText ?? string.Empty),
                VisibleCount: AssistantVisibleCount);
        }
    }

    public string? GetNewestAssistantHtml()
    {
        lock (_lock)
        {
            if (NewestAssistantText is null)
                return null;

            return $"<div class='ds-message'><div class='ds-markdown ds-assistant-message-main-content'>{NewestAssistantText}</div></div>";
        }
    }

    public int GetMessageItemCount()
    {
        lock (_lock)
        {
            return ItemCount;
        }
    }

    public bool IsStopIndicatorVisible()
    {
        lock (_lock)
        {
            return StopVisible;
        }
    }

    public bool IsInputCleared() => !InputHasText;

    public bool IsSendEnabled()
    {
        lock (_lock)
        {
            return SendEnabled;
        }
    }

    public bool AreAttachmentChipsVisible()
    {
        lock (_lock)
        {
            return ChipsVisible;
        }
    }

    public void InsertText(string text) => InputHasText = true;

    public bool TryClickSend()
    {
        lock (_lock)
        {
            Sends.Add(("click", ChipsVisible));
            if (!SendEnabled)
                return false;
        }

        OnSent?.Invoke();
        return true;
    }

    public void PressEnter()
    {
        lock (_lock)
        {
            Sends.Add(("enter", ChipsVisible));
        }

        OnSent?.Invoke();
    }

    public bool TryUploadFiles(IReadOnlyList<string> filePaths) => !FailUpload;

    public void SaveDebugScreenshot(string name)
    {
    }

    /// <summary>Simulates a successful send: the user message lands and generating starts.</summary>
    public void SimulateUserMessageAccepted()
    {
        lock (_lock)
        {
            InputHasText = false;
            ItemCount++;
            StopVisible = true;
        }
    }

    /// <summary>Simulates the finished reply: stop indicator gone, new assistant message present.</summary>
    public void SimulateAssistantReply(string key, string text, int visibleCount)
    {
        lock (_lock)
        {
            StopVisible = false;
            NewestAssistantKey = key;
            NewestAssistantText = text;
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
