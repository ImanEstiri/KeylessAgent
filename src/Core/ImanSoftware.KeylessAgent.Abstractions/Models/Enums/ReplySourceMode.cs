namespace ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

/// <summary>
/// Where the agent reads the model's reply text from.
/// </summary>
public enum ReplySourceMode
{
    /// <summary>Try the message Copy button first, validate, fall back to the DOM reader.</summary>
    Auto,

    /// <summary>Copy button only; capture or validation failures are errors (never silent fallback).</summary>
    Copy,

    /// <summary>DOM final-answer extraction only (the original behavior).</summary>
    Dom,
}
