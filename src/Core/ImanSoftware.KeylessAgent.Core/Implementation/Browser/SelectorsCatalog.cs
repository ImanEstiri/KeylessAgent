using System.Reflection;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Serialization;
using ImanSoftware.Outcomes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// All chat-page selectors with fallback lists, loaded from the workspace <c>selectors.json</c>
/// so they can be fixed without recompiling. Falls back to the embedded default catalog
/// and writes that default to the workspace when the file is missing.
/// </summary>
internal sealed class SelectorsCatalog
{
    private const string EmbeddedResourceName = "ImanSoftware.KeylessAgent.Core.Resources.selectors.json";

    /// <summary>Input textarea selectors.</summary>
    public string[] InputBox { get; init; } = [];

    /// <summary>Send-button selectors (icon-only button next to the file input).</summary>
    public string[] SendButton { get; init; } = [];

    /// <summary>New-chat button selectors.</summary>
    public string[] NewChatButton { get; init; } = [];

    /// <summary>Message-list item selectors (the list is virtualized; newest messages only).</summary>
    public string[] MessageItems { get; init; } = [];

    /// <summary>Assistant message container selectors.</summary>
    public string[] AssistantMessage { get; init; } = [];

    /// <summary>Final-answer element selectors (never matches inside .ds-think-content).</summary>
    public string[] FinalAnswer { get; init; } = [];

    /// <summary>Reasoning block selectors (excluded from every read).</summary>
    public string[] Reasoning { get; init; } = [];

    /// <summary>Stop / generating indicator selectors.</summary>
    public string[] StopIndicator { get; init; } = [];

    /// <summary>File upload input selectors.</summary>
    public string[] FileInput { get; init; } = [];

    /// <summary>Uploaded-file chip selectors (the upload has landed in the UI).</summary>
    public string[] AttachmentChips { get; init; } = [];

    /// <summary>DeepThink toggle selectors.</summary>
    public string[] DeepThinkToggle { get; init; } = [];

    /// <summary>Search toggle selectors.</summary>
    public string[] SearchToggle { get; init; } = [];

    /// <summary>Login page indicator selectors.</summary>
    public string[] LoginIndicator { get; init; } = [];

    /// <summary>
    /// Loads the catalog: the workspace file when present, else the embedded default
    /// (which is also written to the workspace so it can be edited).
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="selectorsFileName">Workspace-relative selectors file name.</param>
    /// <returns>The catalog, or a <c>KA_CONFIG_INVALID</c> failure with a precise message.</returns>
    public static Outcome<SelectorsCatalog> Load(string workspaceRoot, string selectorsFileName)
    {
        var path = Path.Combine(workspaceRoot, selectorsFileName);
        var storage = ImanFileStorageFactory.CreateStorage();

        if (!storage.FileExists(path))
        {
            var embedded = ReadEmbeddedDefault();
            if (embedded.IsFailure)
                return Outcome.Failure<SelectorsCatalog>(embedded.Error);

            var directory = storage.CreateDirectoryAsync(Path.GetDirectoryName(path)!);
            if (directory.GetAwaiter().GetResult().IsFailure)
                return Outcome.Failure<SelectorsCatalog>(new OutcomeError(
                    $"Cannot create directory for {path}.", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

            var write = storage.WriteAllTextAsync(path, embedded.Data).GetAwaiter().GetResult();
            if (write.IsFailure)
                return Outcome.Failure<SelectorsCatalog>(new OutcomeError(
                    $"Cannot write default selectors to {path}: {write.Error.Message}",
                    ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

            return Deserialize(embedded.Data, path);
        }

        var text = storage.ReadAllTextAsync(path).GetAwaiter().GetResult();
        if (text.IsFailure)
            return Outcome.Failure<SelectorsCatalog>(new OutcomeError(
                $"Cannot read {path}: {text.Error.Message}", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

        return Deserialize(text.Data, path);
    }

    private static Outcome<SelectorsCatalog> Deserialize(string json, string path)
    {
        try
        {
            var serializer = JsonSerializerFactory.CreateSystemTextJsonSerializer(new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Converters = { new JsonStringEnumConverter() },
            });

            var catalog = serializer.Deserialize<SelectorsCatalog>(json);
            if (catalog is null)
            {
                return Outcome.Failure<SelectorsCatalog>(new OutcomeError(
                    $"{path} is empty.", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Validation));
            }

            if (catalog.InputBox.Length == 0 || catalog.FinalAnswer.Length == 0 || catalog.SendButton.Length == 0)
            {
                return Outcome.Failure<SelectorsCatalog>(new OutcomeError(
                    $"{path} must define at least InputBox, SendButton and FinalAnswer selectors.",
                    ErrorCodes.ConfigurationValidation, OutcomeErrorType.Validation));
            }

            return Outcome.Successful(catalog);
        }
        catch (JsonException ex)
        {
            return Outcome.Failure<SelectorsCatalog>(new OutcomeError(
                $"{path} is not valid JSON: {ex.Message}", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Validation));
        }
    }

    private static Outcome<string?> ReadEmbeddedDefault()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            return Outcome.Failure<string?>(new OutcomeError(
                $"Embedded resource {EmbeddedResourceName} is missing.", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Problem));
        }

        using var reader = new StreamReader(stream);
        return Outcome.Successful<string?>(reader.ReadToEnd());
    }
}
