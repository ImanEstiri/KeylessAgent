using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>One selector check row of the doctor report table.</summary>
/// <param name="Key">The selectors.json key.</param>
/// <param name="Status">The raw check status (drives the chip color).</param>
/// <param name="State">The display text (MATCHED / NOT FOUND / NOT APPLICABLE).</param>
/// <param name="Fallback">"#N" of the matching fallback or "-".</param>
/// <param name="Elements">The matched element count or "-".</param>
/// <param name="Detail">The human-readable detail text.</param>
public sealed record DoctorCheckRow(
    string Key,
    SelectorCheckStatus Status,
    string State,
    string Fallback,
    string Elements,
    string Detail);

/// <summary>One summary row above the doctor table (Logged in, toggles, input, all-matched).</summary>
/// <param name="Label">What was probed.</param>
/// <param name="Value">The display value.</param>
/// <param name="Ok">Whether the value is healthy (chip color).</param>
public sealed record DoctorSummaryRow(string Label, string Value, bool Ok);

/// <summary>
/// Pure mapping from a <see cref="ChatDiagnosticsReport"/> to the doctor page rows (the CLI
/// DoctorCommand table, UI-flavored). No WPF types — unit tested directly.
/// </summary>
public static class DoctorRowMapper
{
    /// <summary>Maps every selector check to a table row.</summary>
    /// <param name="checks">The checks of the report.</param>
    public static IReadOnlyList<DoctorCheckRow> MapChecks(IReadOnlyList<SelectorCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return checks.Select(MapCheck).ToList();
    }

    /// <summary>Maps the report's summary values (order matches the CLI output).</summary>
    /// <param name="report">The doctor report.</param>
    public static IReadOnlyList<DoctorSummaryRow> MapSummary(ChatDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return
        [
            new DoctorSummaryRow("Logged in", report.LoggedIn ? "yes" : "NO — the login page is shown", report.LoggedIn),
            new DoctorSummaryRow("DeepThink pressed", report.DeepThinkPressed ?? "not found", report.DeepThinkPressed == "true"),
            new DoctorSummaryRow("Search pressed", report.SearchPressed ?? "not found", report.SearchPressed == "true"),
            new DoctorSummaryRow("Input accepts text", report.InputAcceptsText ? "yes" : "NO", report.InputAcceptsText),
            new DoctorSummaryRow("All required matched", report.AllRequiredMatched ? "yes" : "NO", report.AllRequiredMatched),
        ];
    }

    private static DoctorCheckRow MapCheck(SelectorCheck check) => new(
        check.Key,
        check.Status,
        check.Status switch
        {
            SelectorCheckStatus.Matched => "MATCHED",
            SelectorCheckStatus.NotFound => "NOT FOUND",
            _ => "NOT APPLICABLE NOW",
        },
        check.Status == SelectorCheckStatus.Matched
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#{check.FallbackIndex}")
            : "-",
        check.Status == SelectorCheckStatus.Matched
            ? check.MatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "-",
        check.Detail);
}
