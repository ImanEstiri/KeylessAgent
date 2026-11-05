namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>
/// UI-only settings persisted by <see cref="SettingsService"/> to
/// <c>%APPDATA%\KeylessAgent\wpf-settings.json</c>. Property names are the JSON keys.
/// </summary>
public sealed record WpfSettings
{
    /// <summary>The workspace folder used in the last session; empty on first run.</summary>
    public string LastWorkspace { get; init; } = string.Empty;

    /// <summary>Most recently used workspace folders, newest first; at most 10 entries.</summary>
    public IReadOnlyList<string> RecentWorkspaces { get; init; } = [];

    /// <summary>Disables backdrop spheres and entrance animations on weak machines.</summary>
    public bool ReduceEffects { get; init; }

    /// <summary>Requested system backdrop: Auto, Mica, Acrylic or None.</summary>
    public string BackdropPreference { get; init; } = "Auto";

    /// <summary>Pre-ticks the dry-run checkbox on the agent page.</summary>
    public bool DryRunDefault { get; init; }

    /// <summary>Restored window placement from the last session.</summary>
    public WindowSettings Window { get; init; } = new();
}

/// <summary>
/// The saved window placement. Nullable coordinates mean "no saved value" and are skipped
/// in the JSON file.
/// </summary>
public sealed record WindowSettings
{
    /// <summary>Restored window left edge in DIPs; null keeps the system default.</summary>
    public double? Left { get; init; }

    /// <summary>Restored window top edge in DIPs; null keeps the system default.</summary>
    public double? Top { get; init; }

    /// <summary>Restored window width in DIPs; null keeps the system default.</summary>
    public double? Width { get; init; }

    /// <summary>Restored window height in DIPs; null keeps the system default.</summary>
    public double? Height { get; init; }

    /// <summary>Whether the window was maximized when the last session ended.</summary>
    public bool Maximized { get; init; }
}
