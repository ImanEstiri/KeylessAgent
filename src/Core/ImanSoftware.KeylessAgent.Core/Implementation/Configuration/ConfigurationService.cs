using System.Text.Json;
using System.Text.Json.Serialization;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
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

    public ConfigurationService()
        : this(ImanFileStorageFactory.CreateStorage())
    {
    }

    internal ConfigurationService(IFileStorage storage)
    {
        _storage = storage;
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
            var serializer = CreateSerializer();
            var defaults = serializer.Serialize(new KeylessOptions());

            var mkdir = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(path)!);
            if (mkdir.IsFailure)
                return Outcome.Failure<KeylessOptions>(mkdir.Error);

            var write = await _storage.WriteAllTextAsync(path, defaults);
            if (write.IsFailure)
                return Outcome.Failure<KeylessOptions>(write.Error);

            return Outcome.Successful(new KeylessOptions());
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

        return Outcome.Successful(options);
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
        if (options.Browser.ReplyTimeoutSeconds < 10)
            problems.Add("Browser.ReplyTimeoutSeconds must be at least 10.");
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
