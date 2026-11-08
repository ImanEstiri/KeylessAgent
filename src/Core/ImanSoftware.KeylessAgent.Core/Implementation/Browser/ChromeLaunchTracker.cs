using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Remembers the BrowserOptions of the Chrome session this app LAST started or attached to
/// (set by PlaywrightChatProvider when a connect succeeds). The WPF App.OnExit safety net
/// reads it so the shutdown close matches ownership against the REAL port/profile that were
/// actually used, instead of defaults that only happen to match.
/// </summary>
public static class ChromeLaunchTracker
{
    /// <summary>The options of the most recent successful connect; null when none yet.</summary>
    public static BrowserOptions? Last { get; private set; }

    /// <summary>
    /// The resolved profile directory of the most recent successful connect (empty when
    /// none yet) — the ownership matcher's other half besides the debug port.
    /// </summary>
    public static string LastUserDataDir { get; private set; } = string.Empty;

    /// <summary>Records the options of a successful connect (call AFTER the connect returns).</summary>
    public static void Remember(BrowserOptions options, string resolvedUserDataDir)
    {
        Last = options;
        LastUserDataDir = resolvedUserDataDir;
    }

    /// <summary>Test/diagnostic reset.</summary>
    public static void Reset()
    {
        Last = null;
        LastUserDataDir = string.Empty;
    }
}
