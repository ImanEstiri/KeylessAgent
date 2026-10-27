using AngleSharp;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// doctor --capture sanitizing: personal text and identifying attributes are masked, UI labels
/// and structural attributes survive, and the trimmed HTML parses to the same element structure.
/// </summary>
public class HtmlTrimmerTests
{
    private const string SidebarFragment = """
        <div class="e07f456c">
        <a class="_546d736" href="/a/chat/s/9ecef865-fef1-49f3-9d51-4d212fdf4262"><div class="c08e6e93">DepLens private project</div></a>
        <a class="_546d736" href="/a/chat/s/b1e2b433-a9c5-446d-b12a-37e7aee308e3"><div class="c08e6e93">راهنمای حرفه‌ای لینکدین</div></a>
        <a class="_546d736" href="/a/chat/s/36720af4-0138-4429-85de-ebdea14da4ac"><div class="c08e6e93">Lab report analysis</div></a>
        </div>
        """;

    private const string UserMessageFragment = """
        <div class="ds-message"><div class="ds-markdown ds-markdown--block"><p>fix the parser bug in OrderService.cs line 42</p></div></div>
        """;

    private const string ChipFragment = """
        <div class="f7b03e12" title="tree-view-result.txt"><span class="b8c53f0d">tree-view-result.txt</span><div aria-label="Remove tree-view-result.txt"></div></div>
        """;

    private const string ComposerFragment = """
        <div class="_58b31c9">
        <div tabindex="0" aria-pressed="true" class="ds-toggle-button ds-toggle-button--selected"><span class="_6dbc175">DeepThink</span></div>
        <div tabindex="0" aria-pressed="false" class="ds-toggle-button"><span class="_6dbc175">Search</span></div>
        </div>
        <textarea placeholder="Message DeepSeek" rows="2"></textarea>
        <input type="file" multiple="" accept=".md,.txt,.cs,.json">
        <div role="button" class="ds-button ds-button--filled" aria-label="Stop"></div>
        """;

    [Fact]
    public void Trim_SidebarTitlesAndHrefs_NeverAppear()
    {
        var trimmed = HtmlTrimmer.Trim(SidebarFragment);

        trimmed.Should().NotContain("DepLens private project");
        trimmed.Should().NotContain("لینکدین");
        trimmed.Should().NotContain("Lab report analysis");
        trimmed.Should().NotContain("9ecef865", "href values are masked");
        trimmed.Should().NotContain("b1e2b433");
        trimmed.Should().Contain("[masked]", "the titles are replaced with a mask");
    }

    [Fact]
    public void Trim_UserMessageText_NeverAppears()
    {
        var trimmed = HtmlTrimmer.Trim(UserMessageFragment);

        trimmed.Should().NotContain("OrderService");
        trimmed.Should().NotContain("parser bug");
        trimmed.Should().NotContain("line 42");
        trimmed.Should().Contain("[masked]");
    }

    [Fact]
    public void Trim_AttachmentFileName_NeverAppears()
    {
        var trimmed = HtmlTrimmer.Trim(ChipFragment);

        trimmed.Should().NotContain("tree-view-result.txt");
        trimmed.Should().Contain("[masked]");
    }

    [Fact]
    public void Trim_AllowedUiLabelsAndStructuralAttributes_Survive()
    {
        var trimmed = HtmlTrimmer.Trim(ComposerFragment);

        trimmed.Should().Contain("DeepThink");
        trimmed.Should().Contain("Search");
        trimmed.Should().Contain("Message DeepSeek");
        trimmed.Should().Contain("aria-pressed=\"true\"");
        trimmed.Should().Contain("aria-pressed=\"false\"");
        trimmed.Should().Contain("type=\"file\"");
        trimmed.Should().Contain("multiple=\"\"");
        trimmed.Should().Contain(".md,.txt,.cs,.json");
        trimmed.Should().Contain("role=\"button\"");
        trimmed.Should().Contain("class=\"ds-toggle-button");
    }

    [Fact]
    public void Trim_DataVirtualListItemKey_ValueIsReplacedWithStableMarker()
    {
        var trimmed = HtmlTrimmer.Trim("<div data-virtual-list-item-key=\"441\"><span>secret reply text</span></div>");

        trimmed.Should().NotContain("441");
        trimmed.Should().Contain("data-virtual-list-item-key=\"[key:");
        trimmed.Should().NotContain("secret reply text");
    }

    [Fact]
    public void Trim_LongTextRuns_DoNotSwallowTheFollowingTag()
    {
        var longText = new string('x', 300);
        var html = $"<p>short</p><span>{longText}</span><em>after</em>";

        var trimmed = HtmlTrimmer.Trim(html);

        // The '>' fix: the run is masked, the '<' of the NEXT tag survives, so the structure holds.
        trimmed.Should().NotContain(longText);
        trimmed.Should().NotContain("...<span", "the mask must not eat the following '<'");
    }

    [Fact]
    public async Task Trim_ParsesToTheSameElementStructure()
    {
        var html = """
            <div class="composer">
            <script>var x=1;</script>
            <style>.a{color:red}</style>
            <svg><path d="M0 0"/></svg>
            <textarea placeholder="Message DeepSeek" rows="2"></textarea>
            <input type="file" multiple="" accept=".md">
            <div role="button" class="send ds-button--filled" aria-disabled="false">Send it now please</div>
            <p>some personal text here</p>
            </div>
            """;

        var trimmed = HtmlTrimmer.Trim(html);

        var context = BrowsingContext.New();
        var original = await context.OpenAsync(req => req.Content($"<html><body>{html}</body></html>"));
        var sanitized = await context.OpenAsync(req => req.Content($"<html><body>{trimmed}</body></html>"));

        var originalTags = original.Body!.QuerySelectorAll("*").Select(e => e.TagName).ToList();
        var sanitizedTags = sanitized.Body!.QuerySelectorAll("*").Select(e => e.TagName).ToList();

        // script/style/svg are intentionally removed; everything else must be identical in order.
        var expected = originalTags
            .Where(t => !new[] { "script", "style", "svg", "path" }.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();
        sanitizedTags.Should().Equal(expected);

        // the structural attributes of the send button survive
        var send = sanitized.Body.QuerySelector("div[role='button']");
        send.Should().NotBeNull();
        send!.ClassName.Should().Be("send ds-button--filled");
        send.GetAttribute("aria-disabled").Should().Be("false");
    }

    [Fact]
    public void Trim_ThoughtForSeconds_IsKept()
    {
        var trimmed = HtmlTrimmer.Trim("<span class='_5255ff8'>Thought for 12 seconds</span>");

        trimmed.Should().Contain("Thought for 12 seconds");
    }
}
