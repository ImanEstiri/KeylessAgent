using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>Where a reply's text came from.</summary>
public enum ReplyOrigin
{
    /// <summary>The message Copy button (raw Markdown).</summary>
    Copy,

    /// <summary>DOM final-answer extraction.</summary>
    Dom,

    /// <summary>The copy was rejected by validation; the DOM reader was used.</summary>
    DomAfterRejectedCopy,
}

/// <summary>The validated reply text plus its origin.</summary>
/// <param name="Text">The reply text (raw Markdown for Copy, rendered text for Dom).</param>
/// <param name="Origin">Which source won.</param>
public sealed record ReplyReadResult(string Text, ReplyOrigin Origin);

/// <summary>
/// Inputs for one reply read: the rendered DOM final-answer text, the reasoning text of the
/// same message (for contamination checks), and a delegate performing the whole copy capture
/// (hover + click + interception/clipboard with restore).
/// </summary>
/// <param name="DomFinalAnswer">The rendered final-answer text (already extracted from the DOM).</param>
/// <param name="ReasoningText">The reasoning block text, or null when there is none.</param>
/// <param name="TryCopyAsync">Performs the copy capture; null text means both strategies failed.</param>
public sealed record ReplyReadContext(
    string DomFinalAnswer,
    string? ReasoningText,
    Func<CancellationToken, Task<string?>> TryCopyAsync);

/// <summary>
/// Reply-source strategy and validation: Copy (raw Markdown from the message Copy button)
/// first per the configured mode, validated against the DOM text, with DOM fallback for Auto.
/// </summary>
internal static class ReplyReader
{
    /// <summary>
    /// Reads the reply per the mode. The DOM text is always available (completion detection
    /// depends on it); the copy path produces raw Markdown that becomes the primary parser input.
    /// </summary>
    /// <param name="mode">The configured reply source.</param>
    /// <param name="context">DOM text, reasoning text and the copy delegate.</param>
    /// <param name="logNumbers">Receives a human-readable validation summary for the turn log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The text + origin, or a failure (only when Mode=Copy and the copy path failed
    /// or was rejected — the caller maps it to KA_REPLY_TIMEOUT).</returns>
    public static async Task<Outcome<ReplyReadResult>> ReadAsync(
        ReplySourceMode mode,
        ReplyReadContext context,
        Action<string>? logNumbers = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == ReplySourceMode.Dom)
            return Outcome.Successful(new ReplyReadResult(context.DomFinalAnswer, ReplyOrigin.Dom));

        var copy = await context.TryCopyAsync(cancellationToken);
        if (string.IsNullOrEmpty(copy))
        {
            if (mode == ReplySourceMode.Copy)
            {
                return Outcome.Failure<ReplyReadResult>(new OutcomeError(
                    "The message Copy button produced no text (capture failed).",
                    "KA_REPLY_TIMEOUT", OutcomeErrorType.Failure));
            }

            return Outcome.Successful(new ReplyReadResult(context.DomFinalAnswer, ReplyOrigin.Dom));
        }

        var validation = Validate(copy, context.DomFinalAnswer, context.ReasoningText);
        logNumbers?.Invoke(validation.Summary);
        if (validation.IsValid)
            return Outcome.Successful(new ReplyReadResult(copy, ReplyOrigin.Copy));

        if (mode == ReplySourceMode.Copy)
        {
            return Outcome.Failure<ReplyReadResult>(new OutcomeError(
                $"The copied reply failed validation ({validation.Summary}).",
                "KA_REPLY_TIMEOUT", OutcomeErrorType.Failure));
        }

        return Outcome.Successful(new ReplyReadResult(context.DomFinalAnswer, ReplyOrigin.DomAfterRejectedCopy));
    }

    /// <summary>
    /// Normalized form for comparison: markdown and punctuation stripped, whitespace collapsed,
    /// lowercased.
    /// </summary>
    public static string NormalizeForCompare(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
                builder.Append(' ');
            else if (!IsMarkdownOrPunctuation(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        var normalized = builder.ToString();
        while (normalized.Contains("  ", StringComparison.Ordinal))
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        return normalized.Trim();
    }

    /// <summary>
    /// The validation rules from the spec: (a) containment or similarity >= 0.9;
    /// (b) the reasoning start must not appear in the copy; (c) copy non-empty and
    /// not shorter than 50% of the DOM text.
    /// </summary>
    public static (bool IsValid, string Summary) Validate(string copied, string domText, string? reasoningText)
    {
        var normCopied = NormalizeForCompare(copied);
        var normDom = NormalizeForCompare(domText);

        if (normCopied.Length == 0)
            return (false, "copied normalized text is empty");

        if (normDom.Length > 0 && normCopied.Length * 2 < normDom.Length)
            return (false, $"copied {normCopied.Length} chars is under 50% of DOM {normDom.Length} chars");

        var contained = normDom.Length == 0 ||
                        normCopied.Contains(normDom, StringComparison.Ordinal);
        var similarity = normDom.Length == 0 ? 1.0 : Similarity(normCopied, normDom);
        var contentOk = contained || similarity >= 0.9;

        var reasoningNorm = reasoningText is null ? string.Empty : NormalizeForCompare(reasoningText);
        var reasoningStart = reasoningNorm.Length > 0
            ? reasoningNorm[..Math.Min(60, reasoningNorm.Length)]
            : string.Empty;
        var reasoningClean = reasoningStart.Length == 0 || !normCopied.Contains(reasoningStart, StringComparison.Ordinal);

        var summary = $"copied={normCopied.Length} dom={normDom.Length} similarity={similarity:F2} " +
                      $"contained={contained} reasoningClean={reasoningClean}";
        return (contentOk && reasoningClean, summary);
    }

    /// <summary>
    /// Token-overlap similarity (linear in the input size): Jaccard over word tokens,
    /// combined with the length ratio.
    /// </summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
            return 1.0;
        if (a.Length == 0 || b.Length == 0)
            return 0.0;

        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokensB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var setB = new HashSet<string>(tokensB, StringComparer.Ordinal);
        var overlap = tokensA.Count(setB.Contains);
        var jaccard = (double)overlap / (tokensA.Length + tokensB.Length - overlap);
        var lengthRatio = (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
        return (jaccard + lengthRatio) / 2.0;
    }

    private static bool IsMarkdownOrPunctuation(char c)
        => !char.IsLetterOrDigit(c);
}
