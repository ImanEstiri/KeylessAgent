using System.Text;
using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Extracts the final-answer text from one assistant message's HTML.
/// A tiny tag scanner — not a full DOM: it tracks the open-element stack, skips the
/// reasoning subtree (<c>.ds-think-content</c>) entirely, keeps code-block text only from
/// the <c>&lt;pre&gt;</c> (never the Copy/Download buttons) and preserves code indentation
/// by emitting raw text nodes instead of rendered <c>innerText</c>.
/// </summary>
internal sealed class ReplyExtractor : IReplyExtractor
{
    private const string FinalAnswerClass = "ds-assistant-message-main-content";
    private const string ReasoningClass = "ds-think-content";
    private const string CodeBlockClass = "md-code-block";

    private static readonly HashSet<string> BlockTags =
        ["p", "div", "li", "ul", "ol", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "table", "blockquote", "hr", "br"];

    /// <inheritdoc />
    public string? ExtractFinalAnswer(string messageOuterHtml)
    {
        if (string.IsNullOrWhiteSpace(messageOuterHtml))
            return null;

        var tokens = Tokenize(messageOuterHtml);
        var stack = new List<ElementInfo>();
        var output = new StringBuilder();
        var foundFinalAnswer = false;
        var inFinalAnswer = false;

        foreach (var token in tokens)
        {
            if (token is TextToken text)
            {
                // Inside a code block, only the <pre> carries text; the banner (Copy/Download buttons) is skipped.
                var inCodeBlockBanner = stack.Any(e => e.IsCodeBlock) && !stack.Any(e => e.IsPre);
                var elementAllows = !stack.Any(e => e.IsReasoning) &&
                                    !inCodeBlockBanner &&
                                    (inFinalAnswer || stack.Any(e => e.IsFinalAnswer));
                if (!elementAllows)
                    continue;

                if (stack.Any(e => e.IsPre))
                    output.Append(DecodeEntities(text.Value));
                else
                    output.Append(DecodeEntities(Whitespace(text.Value)));
                continue;
            }

            var tag = (TagToken)token;
            if (tag.IsEndTag)
            {
                for (var i = stack.Count - 1; i >= 0; i--)
                {
                    if (stack[i].TagName == tag.TagName)
                    {
                        var closing = stack[i];
                        stack.RemoveAt(i);
                        if (closing.IsFinalAnswer)
                            inFinalAnswer = false;
                        if (BlockTags.Contains(tag.TagName) && output.Length > 0)
                            output.Append('\n');
                        break;
                    }
                }

                continue;
            }

            var info = new ElementInfo(
                tag.TagName,
                IsFinalAnswer: tag.ClassList.Contains(FinalAnswerClass),
                IsReasoning: tag.ClassList.Contains(ReasoningClass),
                IsCodeBlock: tag.ClassList.Contains(CodeBlockClass),
                IsPre: tag.TagName == "pre");

            if (info.IsFinalAnswer)
            {
                foundFinalAnswer = true;
                inFinalAnswer = true;
            }

            if (tag.IsSelfClosing || tag.TagName is "br" or "img" or "input" or "hr")
            {
                if (tag.TagName == "br" && !stack.Any(e => e.IsReasoning) && (inFinalAnswer || stack.Any(e => e.IsFinalAnswer)))
                    output.Append('\n');
                continue;
            }

            if (BlockTags.Contains(tag.TagName) && !stack.Any(e => e.IsReasoning) && (inFinalAnswer || stack.Any(e => e.IsFinalAnswer)) && output.Length > 0)
                output.Append('\n');

            if (tag.TagName == "li" && (inFinalAnswer || stack.Any(e => e.IsFinalAnswer)) && !stack.Any(e => e.IsReasoning))
                output.Append("- ");

            stack.Add(info);
        }

        if (!foundFinalAnswer)
            return null;

        // Only trim newlines: leading spaces may be the first line of an indented code block.
        return output.ToString()
            .Replace("\n\n\n", "\n\n", StringComparison.Ordinal)
            .Trim('\r', '\n');
    }

    private static string Whitespace(string value)
        => value.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    private static IEnumerable<HtmlToken> Tokenize(string html)
    {
        var index = 0;
        while (index < html.Length)
        {
            var open = html.IndexOf('<', index);
            if (open < 0)
            {
                yield return new TextToken(html[index..]);
                yield break;
            }

            if (open > index)
                yield return new TextToken(html[index..open]);

            var close = html.IndexOf('>', open);
            if (close < 0)
                yield break;

            var inner = html[(open + 1)..close];
            index = close + 1;

            if (inner.Length == 0)
                continue;

            var isEndTag = inner[0] == '/';
            var isSelfClosing = inner[^1] == '/';
            var body = inner;
            if (isEndTag)
                body = inner[1..];
            else if (isSelfClosing)
                body = inner[..^1];
            var space = body.IndexOf(' ');
            var tagName = (space < 0 ? body : body[..space]).Trim().ToLowerInvariant();
            var classValue = string.Empty;
            if (space >= 0)
            {
                var attributes = body[(space + 1)..];
                var classIndex = attributes.IndexOf("class", StringComparison.OrdinalIgnoreCase);
                if (classIndex >= 0)
                {
                    var quoteStart = attributes.IndexOf('"', classIndex);
                    if (quoteStart < 0)
                        quoteStart = attributes.IndexOf('\'', classIndex);
                    if (quoteStart >= 0)
                    {
                        var quote = attributes[quoteStart];
                        var quoteEnd = attributes.IndexOf(quote, quoteStart + 1);
                        if (quoteEnd > quoteStart)
                            classValue = attributes[(quoteStart + 1)..quoteEnd];
                    }
                }
            }

            yield return new TagToken(
                tagName,
                classValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                isEndTag,
                isSelfClosing);
        }
    }

    private sealed record ElementInfo(
        string TagName,
        bool IsFinalAnswer,
        bool IsReasoning,
        bool IsCodeBlock,
        bool IsPre);

    private abstract record HtmlToken(string Raw);

    private sealed record TextToken(string Value) : HtmlToken(Value);

    private sealed record TagToken(string TagName, string[] ClassList, bool IsEndTag, bool IsSelfClosing) : HtmlToken(TagName);

    private static string DecodeEntities(string value)
        => value
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
}
