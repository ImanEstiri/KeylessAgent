using System.Text.Json;
using System.Text.Json.Serialization;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;
using ImanSoftware.Serialization;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Configuration;

/// <summary>
/// Loads <c>keyless.json</c> tolerantly (comments, case-insensitive keys, trailing commas,
/// string enums), writes the default file when missing, and validates every value.
/// </summary>
internal sealed class ConfigurationService : IConfigurationService
{
    private const string ConfigFileName = "keyless.json";

    private readonly IFileStorage _storage;
    private readonly ImanSoftware.Logging.ILoggerAdapter<ConfigurationService>? _log;

    public ConfigurationService()
        : this(ImanFileStorageFactory.CreateStorage())
    {
    }

    internal ConfigurationService(IFileStorage? storage = null, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        _storage = storage ?? ImanFileStorageFactory.CreateStorage();
        _log = loggerFactory is null
            ? null
            : new ImanSoftware.Logging.LoggerAdapter<ConfigurationService>(loggerFactory);
    }

    /// <summary>
    /// The JSON options every configuration file uses (also used for session.json and turn logs).
    /// </summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static IImanJsonSerializer CreateSerializer()
        => JsonSerializerFactory.CreateSystemTextJsonSerializer(JsonOptions);

    /// <inheritdoc />
    public async Task<Outcome<KeylessOptions>> LoadAsync(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, ConfigFileName);

        if (!_storage.FileExists(path))
        {
            // A fresh file is born at the CURRENT schema version; writing a versionless
            // default here would make the very next load "migrate" it pointlessly.
            var serializer = CreateSerializer();
            var defaults = KeylessOptions.CreateDefault();

            var mkdir = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(path)!);
            if (mkdir.IsFailure)
                return Outcome.Failure<KeylessOptions>(mkdir.Error);

            var write = await _storage.WriteAllTextAsync(path, serializer.Serialize(defaults));
            if (write.IsFailure)
                return Outcome.Failure<KeylessOptions>(write.Error);

            return Outcome.Successful(defaults);
        }

        var read = await _storage.ReadAllTextAsync(path);
        if (read.IsFailure)
            return Outcome.Failure<KeylessOptions>(new OutcomeError(
                $"Cannot read {path}: {read.Error.Message}", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

        if (string.IsNullOrWhiteSpace(read.Data))
            return Outcome.Successful(new KeylessOptions());

        KeylessOptions? options;
        try
        {
            options = CreateSerializer().Deserialize<KeylessOptions>(read.Data);
        }
        catch (JsonException ex)
        {
            return Outcome.Failure<KeylessOptions>(new OutcomeError(
                $"{path} is not valid JSON: {ex.Message}", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Validation));
        }

        if (options is null)
            return Outcome.Successful(new KeylessOptions());

        var validation = Validate(options);
        if (validation.IsFailure)
            return Outcome.Failure<KeylessOptions>(validation.Error);

        var (migratedOptions, migrated) = await MigrateIfStaleAsync(path, read.Data, options);
        LastLoadMigratedSchema = migrated;

        var (upgradedOptions, upgradedFields) = await UpgradeKnownStaleDefaultsAsync(path, read.Data, migratedOptions);
        LastLoadUpgradedFields = upgradedFields;
        return Outcome.Successful(upgradedOptions);
    }

    /// <inheritdoc />
    public bool LastLoadMigratedSchema { get; private set; }

    /// <summary>
    /// The known-stale-default upgrades applied during the LAST load ("Field: old -> new"),
    /// empty when none ran. A full schema migration already rewrites every value, so this
    /// only ever fires for files that are already at the current SchemaVersion.
    /// </summary>
    public IReadOnlyList<string> LastLoadUpgradedFields { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// One entry per field whose SHIPPED default changed after users already wrote their
    /// keyless.json: since CreateDefault() serializes every field explicitly, the old
    /// default literal sits in existing files forever and silently overrides the new code
    /// default. An entry upgrades the loaded value ONLY when it exactly matches a known old
    /// default — anything else means the user deliberately set it and is left alone.
    /// PATTERN for future default changes: when you change a default in KeylessOptions,
    /// add ONE line here (old value(s) -> new default) so existing workspaces pick it up.
    /// Run on EVERY load, orthogonal to the SchemaVersion-gated migration above. String
    /// comparison is OrdinalIgnoreCase — case variants of a shipped default (the tolerant
    /// parser accepts them) are still that default, not a user customization.
    /// </summary>
    private sealed record KnownStaleDefault(
        string FieldPath,
        object? OldDefault,
        object? NewDefault,
        Func<KeylessOptions, object?> Read,
        Func<KeylessOptions, object?, KeylessOptions> With);

    private static readonly KnownStaleDefault[] KnownStaleDefaults =
    [
        // The window default changed twice (Minimized -> Offscreen -> TinyCorner); both old
        // literals throttle Chrome's renderer and truncate replies (turn-17/18 evidence).
        new("Browser.WindowMode", "Minimized", "TinyCorner",
            o => o.Browser.WindowMode,
            (o, v) => o with { Browser = o.Browser with { WindowMode = (string)v! } }),
        new("Browser.WindowMode", "Offscreen", "TinyCorner",
            o => o.Browser.WindowMode,
            (o, v) => o with { Browser = o.Browser with { WindowMode = (string)v! } }),
    ];

    private static bool MatchesOldDefault(object? current, object? oldDefault)
        => oldDefault is string oldString
            ? current is string currentString && string.Equals(currentString, oldString, StringComparison.OrdinalIgnoreCase)
            : Equals(current, oldDefault);

    /// <summary>
    /// Upgrades known-stale default literals (see <see cref="KnownStaleDefaults"/>) on every
    /// load. Backs up the raw file as keyless.json.pre-defaults-upgrade.bak (a suffix that
    /// cannot collide with the real schema-version backups) and rewrites the file with the
    /// upgraded options — every OTHER loaded value (user customizations included) survives,
    /// because the options object came from the user's file.
    /// </summary>
    private async Task<(KeylessOptions Options, IReadOnlyList<string> Upgraded)> UpgradeKnownStaleDefaultsAsync(
        string path, string rawContents, KeylessOptions loaded)
    {
        var upgrades = new List<string>();
        foreach (var entry in KnownStaleDefaults)
        {
            var current = entry.Read(loaded);
            if (!MatchesOldDefault(current, entry.OldDefault) || Equals(current, entry.NewDefault))
                continue;

            loaded = entry.With(loaded, entry.NewDefault);
            upgrades.Add($"{entry.FieldPath}: {current} -> {entry.NewDefault}");
        }

        if (upgrades.Count == 0)
            return (loaded, Array.Empty<string>());

        var backupWrite = await _storage.WriteAllTextAsync($"{path}.pre-defaults-upgrade.bak", rawContents);
        if (backupWrite.IsFailure)
            return (loaded, Array.Empty<string>()); // never fail a load over the upgrade

        var write = await _storage.WriteAllTextAsync(path, CreateSerializer().Serialize(loaded));
        if (write.IsFailure)
            return (loaded, Array.Empty<string>());

        // One informational line per upgraded field, naming exactly what changed and why.
        foreach (var upgrade in upgrades)
            _log?.LogInformation("keyless.json defaults upgrade: {Upgrade} (old shipped default)", upgrade);

        return (loaded, upgrades);
    }

    /// <summary>
    /// Migrates a pre-schema-version keyless.json to the current defaults: backs up the raw
    /// file as keyless.json.v{version}.bak, preserves the four most common user
    /// customizations (Mode, ReplyTimeoutSeconds, ChromeDebugPort, Allowlist) and replaces
    /// every other value — including the stale timeout defaults — with the current ones.
    /// </summary>
    private async Task<(KeylessOptions Options, bool Migrated)> MigrateIfStaleAsync(
        string path, string rawContents, KeylessOptions loaded)
    {
        var effectiveVersion = loaded.SchemaVersion ?? 1;
        if (effectiveVersion >= KeylessOptions.CurrentSchemaVersion)
            return (loaded, false);

        var backupPath = $"{path}.v{effectiveVersion}.bak";
        var backupWrite = await _storage.WriteAllTextAsync(backupPath, rawContents);
        if (backupWrite.IsFailure)
            return (loaded, false);

        var fresh = KeylessOptions.CreateDefault();
        if (loaded.Mode != ApprovalMode.Ask)
            fresh = fresh with { Mode = loaded.Mode };
        if (loaded.Browser.ReplyTimeoutSeconds != 300)
            fresh = fresh with { Browser = fresh.Browser with { ReplyTimeoutSeconds = loaded.Browser.ReplyTimeoutSeconds } };
        if (loaded.Browser.ChromeDebugPort != 9222)
            fresh = fresh with { Browser = fresh.Browser with { ChromeDebugPort = loaded.Browser.ChromeDebugPort } };
        if (!loaded.Commands.Allowlist.SequenceEqual(
                new[] { "dotnet", "git", "npm", "node", "python" }, StringComparer.OrdinalIgnoreCase))
            fresh = fresh with { Commands = fresh.Commands with { Allowlist = loaded.Commands.Allowlist } };

        var serializer = CreateSerializer();
        var write = await _storage.WriteAllTextAsync(path, serializer.Serialize(fresh));
        if (write.IsFailure)
            return (loaded, false);

        return (fresh, true);
    }

    /// <inheritdoc />
    public async Task<Outcome> SaveApprovalModeAsync(string workspaceRoot, ApprovalMode mode)
    {
        var path = Path.Combine(workspaceRoot, ConfigFileName);
        if (!_storage.FileExists(path))
        {
            // No file yet: write the defaults with the requested mode applied.
            var fresh = KeylessOptions.CreateDefault() with { Mode = mode };
            var mkdir = await _storage.CreateDirectoryAsync(workspaceRoot);
            if (mkdir.IsFailure)
                return Outcome.Failure(mkdir.Error);
            return await _storage.WriteAllTextAsync(path, CreateSerializer().Serialize(fresh));
        }

        var read = await _storage.ReadAllTextAsync(path);
        if (read.IsFailure)
            return Outcome.Failure(read.Error);

        KeylessOptions? options;
        try
        {
            options = CreateSerializer().Deserialize<KeylessOptions>(read.Data);
        }
        catch (JsonException ex)
        {
            return Outcome.Failure(new OutcomeError(
                $"{path} is not valid JSON: {ex.Message}", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Validation));
        }

        if (options is null)
            return Outcome.Failure(new OutcomeError(
                $"{path} is empty.", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Validation));

        if (options.Mode == mode)
            return Outcome.Successful(); // nothing to change

        var updated = options with { Mode = mode };
        var write = await _storage.WriteAllTextAsync(path, CreateSerializer().Serialize(updated));
        return write.IsSuccess
            ? Outcome.Successful()
            : Outcome.Failure(write.Error);
    }

    /// <summary>
    /// Range and consistency checks with precise messages.
    /// </summary>
    internal static Outcome Validate(KeylessOptions options)
    {
        var problems = new List<string>();

        if (options.Limits.MaxCommandsPerResponse is < 1 or > 50)
            problems.Add("Limits.MaxCommandsPerResponse must be between 1 and 50.");
        if (options.Limits.MaxResponseChars < 1000)
            problems.Add("Limits.MaxResponseChars must be at least 1000.");
        if (options.Limits.MaxFileContentChars < 100)
            problems.Add("Limits.MaxFileContentChars must be at least 100.");
        if (options.Limits.MaxCommandOutputChars < 100)
            problems.Add("Limits.MaxCommandOutputChars must be at least 100.");
        if (options.Limits.MaxFormatRetries is < 0 or > 10)
            problems.Add("Limits.MaxFormatRetries must be between 0 and 10.");
        if (options.Limits.MaxIterationsPerTurn is < 1 or > 1000)
            problems.Add("Limits.MaxIterationsPerTurn must be between 1 and 1000.");

        if (options.Browser.ChromeDebugPort is < 1 or > 65535)
            problems.Add("Browser.ChromeDebugPort must be a valid port.");
        if (options.Browser.MinDelayMs < 0 || options.Browser.MaxDelayMs < options.Browser.MinDelayMs)
            problems.Add("Browser delays must satisfy 0 <= MinDelayMs <= MaxDelayMs.");
        if (options.Browser.ReplyTimeoutSeconds < 10)
            problems.Add("Browser.ReplyTimeoutSeconds must be at least 10.");
        if (options.Browser.SendConfirmSeconds < 1)
            problems.Add("Browser.SendConfirmSeconds must be at least 1.");
        if (options.Browser.UploadWaitSeconds < 5)
            problems.Add("Browser.UploadWaitSeconds must be at least 5.");
        if (options.Browser.TruncationBudgetSeconds < 5)
            problems.Add("Browser.TruncationBudgetSeconds must be at least 5.");
        if (options.Browser.MinDelayMs < 0 || options.Browser.MaxDelayMs < options.Browser.MinDelayMs)
            problems.Add("Browser delays must satisfy 0 <= MinDelayMs <= MaxDelayMs.");
        if (options.Browser.MaxMessagesPerMinute < 1)
            problems.Add("Browser.MaxMessagesPerMinute must be at least 1.");
        if (string.IsNullOrWhiteSpace(options.Browser.SelectorsFile))
            problems.Add("Browser.SelectorsFile must not be empty.");

        if (string.IsNullOrWhiteSpace(options.Git.BranchPrefix))
            problems.Add("Git.BranchPrefix must not be empty.");
        if (options.Git.ProtectedBranchPatterns.Count == 0)
            problems.Add("Git.ProtectedBranchPatterns must contain at least one pattern.");

        if (options.Commands.TimeoutSeconds < 1)
            problems.Add("Commands.TimeoutSeconds must be at least 1.");

        if (problems.Count > 0)
        {
            return Outcome.Failure(new OutcomeError(
                "Invalid keyless.json: " + string.Join(" ", problems),
                ErrorCodes.ConfigurationValidation, OutcomeErrorType.Validation));
        }

        return Outcome.Successful();
    }
}
