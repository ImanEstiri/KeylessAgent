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
    public async Task<bool> IsInputEditableAsync(CancellationToken cancellationToken = default)
    {
        foreach (var selector in (string[])[
                     "textarea[placeholder=\"Message DeepSeek\"]",
                     "textarea"])
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var locator = _page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 2_000,
                });

                var readOnly = await EvaluateBoolAsync(locator, "element => element.readOnly", cancellationToken);
                var disabled = await EvaluateBoolAsync(locator, "element => element.disabled", cancellationToken);
                return !readOnly && !disabled;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Try the next fallback.
            }
        }

        return false;
    }

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
