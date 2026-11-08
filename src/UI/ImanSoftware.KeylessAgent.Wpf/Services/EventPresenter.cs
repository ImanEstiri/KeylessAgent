using System.Text.RegularExpressions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>
/// Visual weight of a progress event in the feed: a normal step, a waiting state (pulsing),
/// a successful completion, or a warning.
/// </summary>
public enum EventKind
{
    /// <summary>A normal progress step.</summary>
    Step = 0,

    /// <summary>A wait state (pulsing dot): reply wait, user question.</summary>
    Waiting = 1,

    /// <summary>A successful completion (check mark).</summary>
    Success = 2,

    /// <summary>A warning (resend request).</summary>
    Warning = 3,
}

/// <summary>The presentation of one progress event: label, symbol and kind.</summary>
public sealed record EventPresentation(string Label, string Symbol, EventKind Kind);

/// <summary>
/// Pure mapping from <see cref="AgentEventType"/> to feed presentation (the Phase 6 table of
/// the guide) plus the <c>#N Kind Target</c> detail parser for executed commands.
/// </summary>
public static class EventPresenter
{
    /// <summary>Matches details like "#1 RunCommand mkdir y" reported for ExecutingCommand events.</summary>
    private static readonly Regex CommandDetail = new(
        "^#(?<Number>[0-9]+)[ ]+(?<Kind>[A-Za-z_][A-Za-z0-9_]*)[ ]+(?<Target>.+)$",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    /// <summary>Maps a progress event to its feed presentation. Unknown event types never crash.</summary>
    /// <param name="progressEvent">The event reported by Core.</param>
    public static EventPresentation Present(AgentProgressEvent progressEvent)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        return progressEvent.Type switch
        {
            AgentEventType.SessionStarting => new EventPresentation("Preparing the session", "●", EventKind.Step),
            AgentEventType.ConnectingToBrowser => new EventPresentation("Connecting to Chrome", "●", EventKind.Step),
            AgentEventType.OpeningChat => new EventPresentation("Opening the chat", "●", EventKind.Step),
            AgentEventType.UploadingAttachments => new EventPresentation("Uploading project snapshot", "▲", EventKind.Step),
            AgentEventType.SendingMessage => new EventPresentation("Sending your request", "▶", EventKind.Step),
            AgentEventType.WaitingForReply => new EventPresentation("Waiting for the model", "●", EventKind.Waiting),
            AgentEventType.ValidatingReply => new EventPresentation("Checking the reply", "✓", EventKind.Step),
            AgentEventType.RequestingResend => new EventPresentation("Asking the model to fix its reply", "⚠", EventKind.Warning),
            AgentEventType.ExecutingCommand => new EventPresentation("Running a command", "▶", EventKind.Step),
            AgentEventType.AskingUser => new EventPresentation("Waiting for you", "●", EventKind.Waiting),
            AgentEventType.SendingResults => new EventPresentation("Sending results to the model", "▶", EventKind.Step),
            AgentEventType.RefreshingSnapshot => new EventPresentation("Updating the project snapshot", "●", EventKind.Step),
            AgentEventType.TurnFinished => new EventPresentation("Finished", "✓", EventKind.Success),
            _ => new EventPresentation(progressEvent.Type.ToString(), "●", EventKind.Step),
        };
    }

    /// <summary>
    /// Extracts the tier from an auto-approval detail ("Running: cmd (auto-approved: tier)");
    /// empty when the detail is not an auto-approval.
    /// </summary>
    public static string AutoApprovedTier(string? detail)
    {
        if (detail is null)
            return string.Empty;

        var marker = "(auto-approved:";
        var start = detail.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var valueStart = start + marker.Length;
        var close = detail.IndexOf(')', valueStart);
        return close > valueStart ? detail[valueStart..close].Trim() : string.Empty;
    }

    /// <summary>
    /// Parses the ExecutingCommand detail "#1 RunCommand mkdir y" into number, kind and target.
    /// Returns null when the text does not match (the raw text is shown instead).
    /// </summary>
    /// <param name="detail">The progress detail text; may be null.</param>
    public static (int Number, string Kind, string Target)? TryParseCommandDetail(string? detail)
    {
        if (detail is null)
            return null;

        var match = CommandDetail.Match(detail);
        if (!match.Success)
            return null;

        var number = int.Parse(match.Groups["Number"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (number, match.Groups["Kind"].Value, match.Groups["Target"].Value.Trim());
    }
}
