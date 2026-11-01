using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>One read-only key/value row of a settings group.</summary>
public sealed record SettingRow(string Key, string Value);

/// <summary>A group of read-only settings rows (Mode, Browser, Limits, Snapshot, Git, Commands).</summary>
public sealed record SettingsGroup(string Title, IReadOnlyList<SettingRow> Rows);

/// <summary>
/// Pure mapping from <see cref="KeylessOptions"/> to the grouped read-only display on the
/// settings page. No WPF types — unit tested directly.
/// </summary>
public static class SettingsGroupMapper
{
    /// <summary>Maps the loaded options to the grouped display (guide order: Mode, Browser, Limits, Snapshot, Git, Commands).</summary>
    /// <param name="options">The loaded configuration.</param>
    public static IReadOnlyList<SettingsGroup> Map(KeylessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return
        [
            new SettingsGroup("Mode",
            [
                new SettingRow("Approval", options.Mode.ToString()),
                new SettingRow("Pace", options.Agent.Pace.ToString()),
            ]),
            new SettingsGroup("Browser",
            [
                Row("ChromeDebugPort", options.Browser.ChromeDebugPort),
                Row("UserDataDir", options.Browser.UserDataDir.Length == 0 ? "(default profile)" : options.Browser.UserDataDir),
                Row("AutoLaunchChrome", options.Browser.AutoLaunchChrome),
                Row("ReuseExistingTab", options.Browser.ReuseExistingTab),
                Row("MaxTurnsPerChat", options.Browser.MaxTurnsPerChat),
                Row("AllowPlaywrightLaunch", options.Browser.AllowPlaywrightLaunch),
                Row("CloseChromeOnExit", options.Browser.CloseChromeOnExit),
                Row("EnableDeepThink", options.Browser.EnableDeepThink),
                Row("EnableSearch", options.Browser.EnableSearch),
                Row("ReplySource", options.Browser.ReplySource.ToString()),
                Row("WindowMode", options.Browser.WindowMode),
                Row("ReplyTimeoutSeconds", options.Browser.ReplyTimeoutSeconds),
                Row("SendConfirmSeconds", options.Browser.SendConfirmSeconds),
                Row("Debug", options.Browser.Debug),
            ]),
            new SettingsGroup("Limits",
            [
                Row("MaxCommandsPerResponse", options.Limits.MaxCommandsPerResponse),
                Row("MaxResponseChars", options.Limits.MaxResponseChars),
                Row("MaxFileContentChars", options.Limits.MaxFileContentChars),
                Row("MaxCommandOutputChars", options.Limits.MaxCommandOutputChars),
                Row("MaxFormatRetries", options.Limits.MaxFormatRetries),
                Row("MaxIterationsPerTurn", options.Limits.MaxIterationsPerTurn),
            ]),
            new SettingsGroup("Snapshot",
            [
                Row("Mode", options.Snapshot.Mode.ToString()),
                Row("AttachOnFeedback", options.Snapshot.AttachOnFeedback),
                Row("RefreshAfterEachBatch", options.Snapshot.RefreshAfterEachBatch),
            ]),
            new SettingsGroup("Git",
            [
                Row("AutoInit", options.Git.AutoInit),
                Row("BranchPrefix", options.Git.BranchPrefix),
                new SettingRow("ProtectedBranchPatterns", string.Join(", ", options.Git.ProtectedBranchPatterns)),
            ]),
            new SettingsGroup("Commands",
            [
                new SettingRow("Allowlist", string.Join(", ", options.Commands.Allowlist)),
                new SettingRow("AutoApprove", string.Join(", ", options.Commands.AutoApprove)),
                Row("BatchFailureMode", options.Commands.BatchFailureMode),
                Row("TimeoutSeconds", options.Commands.TimeoutSeconds),
            ]),
        ];
    }

    private static SettingRow Row(string key, object value)
        => new(key, System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
}
