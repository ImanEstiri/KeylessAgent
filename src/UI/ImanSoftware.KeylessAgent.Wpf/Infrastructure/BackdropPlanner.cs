namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>The system backdrop kinds the DWM can draw behind the window.</summary>
internal enum BackdropKind
{
    /// <summary>No system backdrop; the app draws its own gradient.</summary>
    None = 0,

    /// <summary>Windows 11 Mica material (subtle, tinted by the desktop).</summary>
    Mica = 2,

    /// <summary>Windows 11 Acrylic material (more translucent, blurrier).</summary>
    Acrylic = 3,
}

/// <summary>The user's backdrop preference (settings UI arrives in Phase 4).</summary>
internal enum BackdropPreference
{
    /// <summary>Pick the best material the OS supports (Mica on 22H2+, else none).</summary>
    Auto = 0,

    /// <summary>Force Mica when supported.</summary>
    Mica = 1,

    /// <summary>Force Acrylic when supported.</summary>
    Acrylic = 2,

    /// <summary>No system backdrop; the app draws its own gradient.</summary>
    None = 3,
}

/// <summary>The visual plan a given OS/preference/effect combination produces.</summary>
/// <param name="Backdrop">The system backdrop kind to request (None = app-drawn gradient).</param>
/// <param name="ShowOrbs">Whether the blurred orb layer is rendered.</param>
/// <param name="ShowFallbackGradient">Whether the app gradient is shown behind the content.</param>
/// <param name="Animations">Whether entrance/hover animations and the pulsing dot run.</param>
internal sealed record BackdropPlan(
    BackdropKind Backdrop,
    bool ShowOrbs,
    bool ShowFallbackGradient,
    bool Animations);

/// <summary>
/// Pure decision function for the backdrop: no WPF or OS calls, fully unit tested.
/// </summary>
internal static class BackdropPlanner
{
    /// <summary>Windows 11 22H2 (build 22621) introduced DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    public const int MinSystemBackdropBuild = 22621;

    /// <summary>
    /// Decides the visual plan for a Windows build, the user's backdrop preference and the
    /// "Reduce effects" toggle.
    /// </summary>
    /// <param name="osBuild">The OS build number (Environment.OSVersion.Version.Build).</param>
    /// <param name="preference">The user's backdrop preference (Auto, Mica, Acrylic, None).</param>
    /// <param name="reduceEffects">True when the user turned orbs/animations off.</param>
    public static BackdropPlan Decide(int osBuild, string preference, bool reduceEffects)
    {
        if (reduceEffects)
            return new BackdropPlan(BackdropKind.None, ShowOrbs: false, ShowFallbackGradient: true, Animations: false);

        var supportsSystemBackdrop = osBuild >= MinSystemBackdropBuild;
        if (!supportsSystemBackdrop)
        {
            // Windows 10 and early Windows 11: no system backdrop — the blurred orbs provide
            // the frosted look over the app gradient instead.
            return new BackdropPlan(BackdropKind.None, ShowOrbs: true, ShowFallbackGradient: true, Animations: true);
        }

        var preferenceKind = Parse(preference);
        if (preferenceKind == BackdropPreference.None)
            return new BackdropPlan(BackdropKind.None, ShowOrbs: false, ShowFallbackGradient: true, Animations: true);

        var backdrop = preferenceKind == BackdropPreference.Acrylic
            ? BackdropKind.Acrylic
            : BackdropKind.Mica; // Auto and Mica both land on Mica (the calm default).

        return new BackdropPlan(backdrop, ShowOrbs: true, ShowFallbackGradient: false, Animations: true);
    }

    private static BackdropPreference Parse(string preference)
        => Enum.TryParse<BackdropPreference>(preference, ignoreCase: true, out var parsed)
            ? parsed
            : BackdropPreference.Auto;
}
