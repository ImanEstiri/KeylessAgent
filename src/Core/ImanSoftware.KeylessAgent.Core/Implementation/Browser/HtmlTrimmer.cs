using System.Text.RegularExpressions;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Diagnostics-only HTML sanitizing for <c>keyless doctor --capture</c>: removes scripts,
/// styles and inline svg, masks every text node except an allowlist of UI labels, and masks
/// attribute values that could identify content or the user. The sidebar (chat history
/// titles), file names and the user's own messages must never appear in a capture.
/// </summary>
internal static partial class HtmlTrimmer
{
    /// <summary>UI labels that are safe to keep verbatim.</summary>
    internal static readonly IReadOnlyList<string> AllowedLabels =
    [
        "DeepThink", "Search", "New chat", "Stop", "Message DeepSeek",
        "Thought for", "seconds", "Copy", "Download", "Regenerate",
    ];

    /// <summary>Attributes that never carry content or identity.</summary>
    private static readonly HashSet<string> StructuralAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "role", "aria-pressed", "aria-disabled", "aria-expanded",
        "type", "accept", "multiple", "tabindex", "rows", "autocomplete", "name", "id",
    };

    private static readonly Regex ScriptRegex = Compile("<script\\b[^>]*>.*?</script>");
    private static readonly Regex StyleRegex = Compile("<style\\b[^>]*>.*?</style>");
    private static readonly Regex SvgRegex = Compile("<svg\\b[^>]*>.*?</svg>");
    private static readonly Regex CommentRegex = Compile("<!--.*?-->");
    private static readonly Regex AttributeRegex = Compile("([a-zA-Z-]+)\\s*=\\s*(\"[^\"]*\"|'[^']*')");
    private static readonly Regex TextRunRegex = Compile(">(?<text>[^<>]+)<");
    private static readonly Regex LongRunRegex = Compile(">[^<>]{200,}<");

    [GeneratedRegex("Thought for \\d+")]
    private static partial Regex ThoughtForRegex();

    private static Regex Compile(string pattern)
        => new(pattern, RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Sanitizes an HTML fragment for bug reports: structural attributes survive, everything
    /// personal is masked, and the result stays well-formed HTML.
    /// </summary>
    /// <param name="html">Raw HTML.</param>
    /// <returns>Sanitized HTML, or an empty string for empty input.</returns>
    public static string Trim(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var text = ScriptRegex.Replace(html, "<!-- script removed -->");
        text = StyleRegex.Replace(text, "<!-- style removed -->");
        text = SvgRegex.Replace(text, "<!-- svg removed -->");
        text = CommentRegex.Replace(text, string.Empty);

        // Very long runs are cut before attribute masking (defensive; masked again below).
        text = LongRunRegex.Replace(text, ">...<");

        text = AttributeRegex.Replace(text, MaskAttribute);
        text = TextRunRegex.Replace(text, MaskTextRun);
        return text;
    }

    private static string MaskAttribute(Match match)
    {
        var name = match.Groups[1].Value;
        var rawValue = match.Groups[2].Value;
        var inner = rawValue[1..^1];

        if (StructuralAttributes.Contains(name))
            return match.Value;

        // data-virtual-list-item-key survives structurally, but the key itself is an
        // identifier — replace its value with a short deterministic marker.
        if (name.Equals("data-virtual-list-item-key", StringComparison.OrdinalIgnoreCase))
            return $"{name}=\"[key:{StableHash(inner)}]\"";

        // Everything else (href, src, title, alt, value, placeholder, aria-label, data-*)
        // is masked unless it is exactly a short allowlisted UI label.
        return IsAllowedLabel(inner) ? match.Value : $"{name}=\"[masked]\"";
    }

    private static string MaskTextRun(Match match)
    {
        var text = match.Groups["text"].Value;
        return ">" + MaskText(text) + "<";
    }

    private static string MaskText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return text;

        // "Thought for N seconds" is a UI label pattern, not content.
        if (ThoughtForRegex().IsMatch(trimmed) && trimmed.StartsWith("Thought for", StringComparison.Ordinal))
            return trimmed;

        return IsAllowedLabel(trimmed) ? trimmed : "[masked]";
    }

    private static bool IsAllowedLabel(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 40)
            return false;

        foreach (var label in AllowedLabels)
        {
            if (trimmed.Equals(label, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static long StableHash(string value)
    {
        unchecked
        {
            var hash = 17L;
            foreach (var c in value)
                hash = (hash * 31) + c;
            return hash;
        }
    }
}
