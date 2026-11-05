using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// The Playwright-backed page inspector behind <c>keyless doctor</c>. Fully async; Playwright
/// waits race the CancellationToken through <see cref="WithCancellationAsync{T}"/>.
/// </summary>
internal sealed class PlaywrightChatInspector(IPage page) : IChatPageInspector
{
    private readonly IPage _page = page;

    /// <inheritdoc />
    public string Url => _page.Url;

    /// <inheritdoc />
    public async Task<(int Index, int Count)?> ProbeAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < selectors.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var count = await WithCancellationAsync(_page.Locator(selectors[index]).CountAsync(), cancellationToken);
                if (count > 0)
                    return (index, (int)count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A broken selector is a finding, not a crash: try the next fallback.
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<string?> GetToggleStateAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(selectors, cancellationToken);
        if (probe is null)
            return null;

        try
        {
            var value = await WithCancellationAsync(
                _page.Locator(selectors[probe.Value.Index]).First.GetAttributeAsync("aria-pressed"),
                cancellationToken);
            return value?.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsInputEditableAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        if (selectors.Length == 0)
            return false;

        // ONE parallel wait over the whole fallback list (the 2s budget once, never once per
        // fallback selector) via the shared Or-locator helper.
        var match = await PlaywrightChatPage.FirstVisibleAsync(
            _page, selectors, 2_000 * selectors.Length, cancellationToken);
        if (match is null)
            return false;

        var readOnly = await EvaluateBoolAsync(match.Value.Locator, "element => element.readOnly", cancellationToken);
        var disabled = await EvaluateBoolAsync(match.Value.Locator, "element => element.disabled", cancellationToken);
        return !readOnly && !disabled;
    }

    /// <inheritdoc />
    public async Task<string> DumpButtonsAsync(string[] assistantMessageSelectors, CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < assistantMessageSelectors.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var count = (int)await WithCancellationAsync(_page.Locator(assistantMessageSelectors[index]).CountAsync(), cancellationToken);
                if (count == 0)
                    continue;

                var newest = _page.Locator(assistantMessageSelectors[index]).Nth(count - 1);
                var dump = await WithCancellationAsync(newest.EvaluateAsync(DumpButtonsScript), cancellationToken);
                return dump?.ToString() ?? "Not found";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A broken selector is a finding, not a crash: try the next fallback.
            }
        }

        return "Not found";
    }

    /// <summary>
    /// Collects tag/role/label/title/text/classes for every button-like element under the
    /// message element, keeping only entries with at least one identifying attribute.
    /// </summary>
    private const string DumpButtonsScript = """
        element => JSON.stringify(Array.from(element.querySelectorAll('button, [role="button"]')).map(function(b) {
            return {
                tag: b.tagName,
                role: b.getAttribute('role'),
                label: b.getAttribute('aria-label'),
                title: b.getAttribute('title'),
                text: (b.textContent || '').trim().slice(0, 40),
                classes: String(b.className).slice(0, 120)
            };
        }).filter(function(x) { return x.label || x.title || x.text; }))
        """;

    /// <inheritdoc />
    public async Task<string?> CaptureTrimmedAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(selectors, cancellationToken);
        if (probe is null)
            return null;

        try
        {
            var html = await WithCancellationAsync(
                _page.Locator(selectors[probe.Value.Index]).First.EvaluateAsync("element => element.outerHTML"),
                cancellationToken);
            return HtmlTrimmer.Trim(html?.ToString() ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> EvaluateBoolAsync(ILocator locator, string script, CancellationToken cancellationToken)
    {
        try
        {
            var result = await WithCancellationAsync(locator.EvaluateAsync(script), cancellationToken);
            return result is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Playwright's waits carry their own timeouts but no CancellationToken; racing the token
    /// makes Ctrl+C interrupt any in-flight wait promptly.
    /// </summary>
    private static async Task<T> WithCancellationAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => cancellation.TrySetResult(true));
        var completed = await Task.WhenAny(task, cancellation.Task);
        if (completed != task)
            throw new OperationCanceledException(cancellationToken);

        return await task;
    }
}
