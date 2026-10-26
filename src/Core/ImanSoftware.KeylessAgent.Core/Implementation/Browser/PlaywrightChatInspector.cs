using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// The Playwright-backed page inspector behind <c>keyless doctor</c>.
/// </summary>
internal sealed class PlaywrightChatInspector(IPage page) : IChatPageInspector
{
    private readonly IPage _page = page;

    /// <inheritdoc />
    public string Url => _page.Url;

    /// <inheritdoc />
    public (int Index, int Count)? Probe(string[] selectors)
    {
        for (var index = 0; index < selectors.Length; index++)
        {
            try
            {
                var count = _page.Locator(selectors[index]).CountAsync().GetAwaiter().GetResult();
                if (count > 0)
                    return (index, count);
            }
            catch
            {
                // A broken selector is a finding, not a crash: try the next fallback.
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string? GetToggleState(string[] selectors)
    {
        var probe = Probe(selectors);
        if (probe is null)
            return null;

        try
        {
            var value = _page.Locator(selectors[probe.Value.Index])
                .First.GetAttributeAsync("aria-pressed").GetAwaiter().GetResult();
            return value;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool IsInputEditable()
    {
        foreach (var selector in (string[])[
                     "textarea[placeholder=\"Message DeepSeek\"]",
                     "textarea"])
        {
            try
            {
                var locator = _page.Locator(selector).First;
                locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 2_000,
                }).GetAwaiter().GetResult();

                var ro = EvaluateBool(locator, "element => element.readOnly");
                var dis = EvaluateBool(locator, "element => element.disabled");
                return !ro && !dis;
            }
            catch
            {
                // Try the next fallback.
            }
        }

        return false;
    }

    private static bool EvaluateBool(ILocator locator, string script)
    {
        try
        {
            var result = locator.EvaluateAsync(script).GetAwaiter().GetResult();
            return result is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True };
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string? CaptureTrimmed(string[] selectors)
    {
        var probe = Probe(selectors);
        if (probe is null)
            return null;

        try
        {
            var html = _page.Locator(selectors[probe.Value.Index]).First
                .EvaluateAsync("element => element.outerHTML").GetAwaiter().GetResult();
            return HtmlTrimmer.Trim(html?.ToString() ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }
}
