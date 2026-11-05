using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

public class ReplyExtractorTests
{
    private readonly ReplyExtractor _extractor = new();

    /// <summary>
    /// A trimmed fragment of the real DOM sample: one assistant message containing a
    /// reasoning block and a final answer with paragraphs and a code block.
    /// </summary>
    // The raw string below embeds REAL newlines, so a CRLF checkout (gitattributes +
    // autocrlf) puts CR inside the fixture and breaks the LF-expecting extractor
    // assertions. Normalize once here, independent of the working-tree line endings.
    private static string RealSampleMessage => RealSampleMessageRaw.Replace("\r\n", "\n");

    private const string RealSampleMessageRaw =
        """
        <div class="ds-message _63c77b1"><div class="_74c0879"><div class="_245c867"><div class="_5ab5d64"><span class="_5255ff8">Thought for 3 seconds</span></div><div class="e1675d8b ds-think-content"><div class="ds-markdown"><p class="ds-markdown-paragraph"><span>reasoning paragraph one</span></p><p class="ds-markdown-paragraph"><span>reasoning paragraph two</span></p></div></div><div class="ds-markdown ds-assistant-message-main-content"><p class="ds-markdown-paragraph"><span>Final answer intro.</span></p><div class="md-code-block md-code-block-dark"><div class="md-code-block-banner-wrap"><div><span class="code-info-button-text">Copy</span></div></div><pre><span>@@say</span><span>
        </span><span>&lt;&lt;&lt;MSG</span><span>
        </span><span>hello from code</span><span>
        </span><span>MSG&gt;&gt;&gt;</span></pre></div><p class="ds-markdown-paragraph"><span>Final answer outro.</span></p></div></div></div>
        """;

    [Fact]
    public void Extract_FinalAnswerOnly_ReasoningNeverReturned()
    {
        var result = _extractor.ExtractFinalAnswer(RealSampleMessage);

        result.Should().NotBeNull();
        result.Should().Contain("Final answer intro.");
        result.Should().Contain("Final answer outro.");
        result.Should().NotContain("reasoning paragraph");
        result.Should().NotContain("Thought for");
    }

    [Fact]
    public void Extract_CodeBlock_PreservesIndentationAndNewlinesAndSkipsButtons()
    {
        var result = _extractor.ExtractFinalAnswer(RealSampleMessage);

        result.Should().Contain("@@say\n<<<MSG\nhello from code\nMSG>>>");
        result.Should().NotContain("Copy");
    }

    [Fact]
    public void Extract_HtmlEntitiesAreDecoded()
    {
        const string html =
            """<div class="ds-markdown ds-assistant-message-main-content"><p><span>a &amp; b &lt; c</span></p></div>""";

        var result = _extractor.ExtractFinalAnswer(html);

        result.Should().Be("a & b < c");
    }

    [Fact]
    public void Extract_ReasoningOnlyMessage_ReturnsNull()
    {
        const string html =
            """<div class="ds-message"><div class="e1675d8b ds-think-content"><div class="ds-markdown"><p><span>only thinking here</span></p></div></div></div>""";

        _extractor.ExtractFinalAnswer(html).Should().BeNull();
    }

    [Fact]
    public void Extract_NoFinalAnswerElement_ReturnsNull()
    {
        const string html = """<div class="ds-message"><div>still streaming…</div></div>""";

        _extractor.ExtractFinalAnswer(html).Should().BeNull();
    }

    [Fact]
    public void Extract_EmptyInput_ReturnsNull()
    {
        _extractor.ExtractFinalAnswer("  ").Should().BeNull();
    }

    [Fact]
    public void Extract_MultiLineCodeWithLeadingSpaces_KeepsThem()
    {
        const string html =
            """
            <div class="ds-markdown ds-assistant-message-main-content"><pre><span>    indented line
            &nbsp;&nbsp;&nbsp;&nbsp;deeper line</span></pre></div>
            """;

        var result = _extractor.ExtractFinalAnswer(html);

        result.Should().Contain("    indented line");
        result.Should().Contain("deeper line");
    }
}
