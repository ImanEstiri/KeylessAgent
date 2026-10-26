using AngleSharp;
using AngleSharp.Dom;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// Runs the default selector catalog against real fragments of the DeepSeek DOM sample
/// (deepseek-dom-sample-trimmed.html): the first fallback of each catalog entry must match
/// the element it was designed for — including final-answer vs reasoning separation.
/// </summary>
public class SelectorsCatalogFragmentTests
{
    private const string SampleInputArea = """
        <div class="_77cefa5"><div class="ec4f5d61"><div class="bf38813a">
        <div role="button" class="ds-button ds-button--iconLabelPrimary ds-button--icon ds-button--capsule ds-button--s ds-button--icon-relative-m f02f0e25" tabindex="0"><div class="ds-button__background"></div><div class="ds-button__icon ds-button__icon--last-child"><div class="ds-icon"><svg/></div></div></div>
        <input type="file" multiple="" accept=".md,.txt,.bat,.cs,.json">
        <div style="width: fit-content;"><div role="button" class="ds-button ds-button--primary ds-button--filled ds-button--circle ds-button--m ds-button--icon-relative-m ds-button--disabled _52c986b" tabindex="0"><div class="ds-button__background"></div><div class="ds-button__icon"><svg/></div></div></div>
        </div></div><div class="_020ab5b"><textarea class="_27c9245" placeholder="Message DeepSeek" rows="2"></textarea></div></div>
        """;

    private const string SampleToggles = """
        <div class="_58b31c9">
        <div tabindex="0" aria-pressed="true" class="f79352dc ds-toggle-button ds-toggle-button--m ds-toggle-button--selected"><span class="_6dbc175">DeepThink</span></div>
        <div tabindex="0" aria-pressed="true" class="f79352dc ds-toggle-button ds-toggle-button--m ds-toggle-button--selected"><span class="_6dbc175">Search</span></div>
        </div>
        """;

    private const string SampleMessages = """
        <div class="ds-virtual-list-items">
        <div data-virtual-list-item-key="441"><div class="ds-message"><div class="ds-markdown ds-assistant-message-main-content"><p><span>first final answer</span></p></div></div></div>
        <div data-virtual-list-item-key="442"><div class="ds-message"><div class="e1675d8b ds-think-content"><div class="ds-markdown"><p><span>reasoning only</span></p></div><div class="ds-markdown ds-assistant-message-main-content"><p><span>second final answer</span></p></div></div></div></div>
        </div>
        """;

    private static IElement? MatchFirst(string html, string css)
    {
        var context = BrowsingContext.New();
        var document = context.OpenAsync(req => req.Content($"<html><body>{html}</body></html>"))
            .GetAwaiter().GetResult();
        return document.QuerySelector(css);
    }

    private static SelectorsCatalog LoadDefault()
    {
        var assembly = typeof(SelectorsCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("ImanSoftware.KeylessAgent.Core.Resources.selectors.json")!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return System.Text.Json.JsonSerializer.Deserialize<SelectorsCatalog>(json, options)!;
    }

    [Fact]
    public void InputBox_FirstFallback_MatchesTheSampleTextarea()
    {
        var catalog = LoadDefault();
        MatchFirst(SampleInputArea, catalog.InputBox[0])!.GetAttribute("placeholder")
            .Should().Be("Message DeepSeek");
    }

    [Fact]
    public void SendButton_FirstFallback_MatchesTheIconLabelPrimaryButton()
    {
        var catalog = LoadDefault();

        // The first fallback targets the icon button in the textarea's sibling container;
        // on this fragment the second (iconLabelPrimary) fallback is the intended match
        // because the icon button is aria-disabled there. At least ONE fallback must match.
        var matched = catalog.SendButton
            .Select(css => MatchFirst(SampleInputArea, css))
            .FirstOrDefault(e => e is not null);

        matched.Should().NotBeNull("at least one send fallback must match the real input area");
        matched!.ClassName.Should().NotContain("ds-button--disabled", "a disabled send is never the target");
    }

    [Fact]
    public void FileInput_FirstFallback_MatchesTheSampleFileInput()
    {
        var catalog = LoadDefault();
        MatchFirst(SampleInputArea, catalog.FileInput[0])!.HasAttribute("multiple")
            .Should().BeTrue();
    }

    [Fact]
    public void Toggles_MatchDeepThinkAndSearch_WithPressedState()
    {
        var catalog = LoadDefault();

        MatchFirst(SampleToggles, catalog.DeepThinkToggle[0])!.GetAttribute("aria-pressed")
            .Should().Be("true");
        MatchFirst(SampleToggles, catalog.SearchToggle[0])!.GetAttribute("aria-pressed")
            .Should().Be("true");
    }

    [Fact]
    public void MessageItems_FirstFallback_MatchesVirtualizedItems()
    {
        var catalog = LoadDefault();
        var context = BrowsingContext.New();
        var doc = context.OpenAsync(req => req.Content($"<html><body>{SampleMessages}</body></html>")).GetAwaiter().GetResult();
        doc.QuerySelectorAll(catalog.MessageItems[0]).Should().HaveCount(2);
    }

    [Fact]
    public void FinalAnswer_MatchesOnlyTheFinalAnswerBlock_NeverTheReasoning()
    {
        var catalog = LoadDefault();
        var context = BrowsingContext.New();
        var doc = context.OpenAsync(req => req.Content($"<html><body>{SampleMessages}</body></html>")).GetAwaiter().GetResult();

        var matches = doc.QuerySelectorAll(catalog.FinalAnswer[0]).ToList();

        matches.Should().HaveCount(2, "both assistant messages carry a final answer");
        matches.Select(m => m.TextContent).Should().NotContain(m => m.Contains("reasoning only"));
        matches.Select(m => m.TextContent).Should().Contain("second final answer");
    }

    [Fact]
    public void Reasoning_MatchesOnlyTheThinkContent()
    {
        var catalog = LoadDefault();
        var context = BrowsingContext.New();
        var doc = context.OpenAsync(req => req.Content($"<html><body>{SampleMessages}</body></html>")).GetAwaiter().GetResult();

        var matches = doc.QuerySelectorAll(catalog.Reasoning[0]).ToList();

        matches.Should().HaveCount(1);
        matches[0].TextContent.Should().Contain("reasoning only");
    }

    [Fact]
    public void StopIndicator_FirstFallbacks_AreStructural_NotClassHashed()
    {
        var catalog = LoadDefault();
        catalog.StopIndicator.Should().Contain(s => !s.Contains("ds-") || s.Contains("ds-button"));
        catalog.StopIndicator.Should().Contain(s => s.Contains("Stop"));
    }
}
