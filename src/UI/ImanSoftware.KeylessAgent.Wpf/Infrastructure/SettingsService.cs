using System.IO;
using System.Text.Json;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>
/// Persists UI-only settings to <c>%APPDATA%\KeylessAgent\wpf-settings.json</c>.
/// A missing or corrupt file yields defaults; the service never throws on load.
/// </summary>
public sealed class SettingsService
{
    /// <summary>Maximum number of recent workspaces remembered.</summary>
    public const int MaxRecentWorkspaces = 10;

    /// <summary>The settings file name inside the settings folder.</summary>
    public const string FileName = "wpf-settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;

    /// <summary>
    /// Creates the service. Pass a custom folder to redirect persistence (used by tests);
    /// the default is <c>%APPDATA%\KeylessAgent</c>.
    /// </summary>
    public SettingsService(string? folder = null)
    {
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeylessAgent");
        SettingsFilePath = Path.Combine(_folder, FileName);
    }

    /// <summary>Full path of the settings file on disk.</summary>
    public string SettingsFilePath { get; }

    /// <summary>Loads the settings; returns defaults when the file is missing or unreadable.</summary>
    public WpfSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
                return new WpfSettings();

            var loaded = JsonSerializer.Deserialize<WpfSettings>(File.ReadAllText(SettingsFilePath), SerializerOptions);
            return loaded ?? new WpfSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new WpfSettings();
        }
    }

    /// <summary>Writes the settings, creating the settings folder when needed.</summary>
    public void Save(WpfSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    /// <summary>
    /// Returns updated settings with the workspace remembered as most recent: the previous
    /// entry is removed, the new one is inserted at the front, and the list is truncated to
    /// <see cref="MaxRecentWorkspaces"/>. Empty paths are ignored.
    /// </summary>
    public static WpfSettings WithRecentWorkspace(WpfSettings settings, string workspace)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(workspace))
            return settings;

        var trimmed = workspace.Trim();
        var recent = settings.RecentWorkspaces.Where(p => !string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        recent.Insert(0, trimmed);

        return settings with
        {
            LastWorkspace = trimmed,
            RecentWorkspaces = recent.Take(MaxRecentWorkspaces).ToList(),
        };
    }
}
