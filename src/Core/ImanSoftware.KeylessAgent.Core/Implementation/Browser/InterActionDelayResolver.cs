
















using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

internal static class InterActionDelayResolver
{
    public static TimeSpan Resolve(int value, InterActionDelaysOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return TimeSpan.Zero;
        if (value < 0)
            return TimeSpan.FromSeconds(options.InvalidValueFallbackSeconds);
        if (value == 0)
            return TimeSpan.Zero;
        if (value > options.HardCeilingSeconds)
            return TimeSpan.FromSeconds(options.InvalidValueFallbackSeconds);
        return TimeSpan.FromSeconds(value);
    }

    public static TimeSpan AfterExecution(BrowserOptions browser)
        => Resolve(browser.InterActionDelays.AfterExecutionSeconds, browser.InterActionDelays);

    public static TimeSpan AfterUpload(BrowserOptions browser)
        => Resolve(browser.InterActionDelays.AfterUploadSeconds, browser.InterActionDelays);

    public static TimeSpan AfterPaste(BrowserOptions browser)
        => Resolve(browser.InterActionDelays.AfterPasteSeconds, browser.InterActionDelays);
}
// end
