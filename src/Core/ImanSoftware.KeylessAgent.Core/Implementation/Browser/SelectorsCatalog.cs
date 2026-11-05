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
public sealed class SelectorsCatalog
{
    private const string EmbeddedResourceName = "ImanSoftware.KeylessAgent.Core.Resources.selectors.json";

    /// <summary>
    /// The catalog version shipped with THIS build (bump whenever the embedded default
    /// selectors change). A workspace file without the key reads as 0 - written by a
    /// pre-versioning release - and is merged up to the embedded version on the next load.
    /// </summary>
    public const int EmbeddedCatalogVersion = 2;

    /// <summary>The version of the file this catalog was loaded from (0 = pre-versioning file).</summary>
    public int CatalogVersion { get; init; }

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

    /// <summary>
    /// Message-level Copy button selectors (copies ONLY the final answer). Scoped to the
    /// newest assistant message at use time; the bar may appear on hover only.
    /// </summary>
    public string[] MessageCopyButton { get; init; } = [];

    /// <summary>Stop / generating indicator selectors.</summary>
    public string[] StopIndicator { get; init; } = [];

    /// <summary>File upload input selectors.</summary>
    public string[] FileInput { get; init; } = [];

    /// <summary>Uploaded-file chip selectors (the upload has landed in the UI).</summary>
    public string[] AttachmentChips { get; init; } = [];

    /// <summary>
    /// Remove-control selectors INSIDE an attachment chip (button / role=button / aria-label
    /// remove|delete|close|cancel / an x icon). Unverified against the live UI — keyless
    /// doctor prints the closest chip candidate when they match nothing.
    /// </summary>
    public string[] AttachmentRemove { get; init; } = [];

    /// <summary>
    /// Failed-upload chip selectors (chip class containing error|failed|retry, error markers).
    /// Unverified against the live UI — keyless doctor prints the closest chip candidate when
    /// they match nothing.
    /// </summary>
    public string[] AttachmentFailed { get; init; } = [];

    /// <summary>Conversation-too-long notice selectors.</summary>
    public string[] ChatLimitNotice { get; init; } = [];

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
    /// <param name="forceRefresh">Merge even when the file's CatalogVersion is current.</param>
    /// <param name="loggerFactory">When set, the merge logs which keys were refreshed.</param>
    /// <returns>The catalog, or a <c>KA_CONFIG_INVALID</c> failure with a precise message.</returns>
    public static async Task<Outcome<SelectorsCatalog>> LoadAsync(string workspaceRoot, string selectorsFileName, CancellationToken cancellationToken = default,
        bool forceRefresh = false, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        var merged = await MergeAsync(workspaceRoot, selectorsFileName, forceRefresh, loggerFactory, cancellationToken);
        if (merged.IsFailure)
            return Outcome.Failure<SelectorsCatalog>(merged.Error);
        return Outcome.Successful(merged.Data.Catalog);
    }

    /// <summary>
    /// The versioned loader: when the workspace file's CatalogVersion is older than the
    /// embedded default's (or forceRefresh), the file is backed up as
    /// selectors.json.v&lt;oldVersion&gt;.bak and MERGED - every key the user never
    /// customized (its value matches ANY version ever shipped for that key, or the key is
    /// missing entirely) is refreshed to the current embedded value; genuine customizations
    /// survive untouched.
    /// </summary>
    /// <returns>The merged catalog and the names of the refreshed keys.</returns>
    public static async Task<Outcome<(SelectorsCatalog Catalog, IReadOnlyList<string> RefreshedKeys)>> MergeAsync(
        string workspaceRoot, string selectorsFileName, bool forceRefresh,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(workspaceRoot, selectorsFileName);
        var storage = ImanFileStorageFactory.CreateStorage();
        var log = loggerFactory is null
            ? null
            : new ImanSoftware.Logging.LoggerAdapter<SelectorsCatalog>(loggerFactory);

        if (!storage.FileExists(path))
        {
            var embedded = ReadEmbeddedDefault();
            if (embedded.IsFailure)
                return Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(embedded.Error);

            if ((await storage.CreateDirectoryAsync(Path.GetDirectoryName(path)!, cancellationToken)).IsFailure)
                return Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(new OutcomeError(
                    $"Cannot create directory for {path}.", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

            var write = await storage.WriteAllTextAsync(path, embedded.Data, cancellationToken);
            if (write.IsFailure)
                return Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(new OutcomeError(
                    $"Cannot write default selectors to {path}: {write.Error.Message}",
                    ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

            var fresh = Deserialize(embedded.Data, path);
            return fresh.IsSuccess
                ? Outcome.Successful((fresh.Data, (IReadOnlyList<string>)Array.Empty<string>()))
                : Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(fresh.Error);
        }

        var text = await storage.ReadAllTextAsync(path, cancellationToken);
        if (text.IsFailure)
            return Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(new OutcomeError(
                $"Cannot read {path}: {text.Error.Message}", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

        using var workspaceJson = ParseLoose(text.Data);
        if (workspaceJson is null)
            return Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(new OutcomeError(
                $"{path} is not valid JSON.", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Validation));

        using var embeddedJson = ParseLoose(ReadEmbeddedDefault().Data ?? "{}");
        if (embeddedJson is null)
            return Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(new OutcomeError(
                "The embedded default selectors resource is invalid.", ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Problem));

        var workspaceVersion = 0;
        var wsVersion = FindWorkspaceKey(workspaceJson.RootElement, "CatalogVersion");
        if (wsVersion.HasValue && wsVersion.Value.TryGetInt32(out var wsVersionValue))
        {
            workspaceVersion = wsVersionValue;
        }

        if (!forceRefresh && workspaceVersion >= EmbeddedCatalogVersion)
        {
            var current = Deserialize(text.Data, path);
            return current.IsSuccess
                ? Outcome.Successful((current.Data, (IReadOnlyList<string>)Array.Empty<string>()))
                : Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(current.Error);
        }

        // --- Merge: refresh never-customized keys, preserve genuine customizations ---
        // Key matching is CASE-INSENSITIVE (the tolerant parser accepts lowercase keys);
        // workspace-only keys (not in the embedded default) are carried over untouched.
        static JsonElement? FindWorkspaceKey(JsonElement workspace, string name)
        {
            foreach (var property in workspace.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.Clone();
            }

            return null;
        }

        var merged = new Dictionary<string, JsonElement>();
        var refreshedKeys = new List<string>();
        foreach (var workspaceProperty in workspaceJson.RootElement.EnumerateObject())
        {
            var isEmbeddedKey = embeddedJson.RootElement.TryGetProperty(workspaceProperty.Name, out _)
                || embeddedJson.RootElement.EnumerateObject()
                    .Any(e => string.Equals(e.Name, workspaceProperty.Name, StringComparison.OrdinalIgnoreCase));
            if (workspaceProperty.Name.Equals("CatalogVersion", StringComparison.OrdinalIgnoreCase) || isEmbeddedKey)
                continue;
            merged[workspaceProperty.Name] = workspaceProperty.Value.Clone();
        }

        foreach (var key in embeddedJson.RootElement.EnumerateObject())
        {
            if (key.Name == "CatalogVersion" || key.Value.ValueKind != JsonValueKind.Array)
                continue;

            var canonicalEmbedded = Canonical(key.Value);
            var workspaceValue = FindWorkspaceKey(workspaceJson.RootElement, key.Name);
            if (workspaceValue is null)
            {
                merged[key.Name] = key.Value.Clone(); // a key added in a later release
                refreshedKeys.Add(key.Name);
                continue;
            }

            var canonicalWorkspace = Canonical(workspaceValue.Value);
            if (canonicalWorkspace == canonicalEmbedded)
            {
                merged[key.Name] = workspaceValue.Value.Clone(); // already current
                continue;
            }

            var isShippedValue = ShippedValues.TryGetValue(key.Name, out var shipped)
                && shipped.Any(variant => JsonSerializer.Serialize(variant) == canonicalWorkspace);
            if (isShippedValue)
            {
                // The workspace value is identical to SOME version ever shipped for this key:
                // the user never customized it, so the new embedded value is safe to take.
                merged[key.Name] = key.Value.Clone();
                refreshedKeys.Add(key.Name);
                continue;
            }

            merged[key.Name] = workspaceValue.Value.Clone(); // deliberate customization - preserve
        }

        var mergedJson = ToIndentedJson(merged, EmbeddedCatalogVersion);
        var backupWrite = await storage.WriteAllTextAsync($"{path}.v{workspaceVersion}.bak", text.Data, cancellationToken);
        if (backupWrite.IsFailure)
            return Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(new OutcomeError(
                $"Cannot back up {path} before the merge: {backupWrite.Error.Message}",
                ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

        var mergedWrite = await storage.WriteAllTextAsync(path, mergedJson, cancellationToken);
        if (mergedWrite.IsFailure)
            return Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(new OutcomeError(
                $"Cannot write the merged selectors to {path}: {mergedWrite.Error.Message}",
                ErrorCodes.ConfigurationInvalid, OutcomeErrorType.Failure));

        log?.LogInformation(
            "selectors.json upgraded from CatalogVersion {From} to {To}; refreshed keys: {Keys}. Backup: {Backup}.",
            workspaceVersion, EmbeddedCatalogVersion,
            refreshedKeys.Count == 0 ? "(none - only the version marker changed)" : string.Join(", ", refreshedKeys),
            $"{selectorsFileName}.v{workspaceVersion}.bak");

        var catalog = Deserialize(mergedJson, path);
        return catalog.IsSuccess
            ? Outcome.Successful((catalog.Data, (IReadOnlyList<string>)refreshedKeys))
            : Outcome.Failure<(SelectorsCatalog, IReadOnlyList<string>)>(catalog.Error);
    }

    private static JsonDocument? ParseLoose(string? json)
    {
        try
        {
            return JsonDocument.Parse(json ?? "{}", new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whitespace-normalized JSON text, so hand-reformatting still compares equal.</summary>
    private static string Canonical(JsonElement element) => JsonSerializer.Serialize(element);

    private static string ToIndentedJson(Dictionary<string, JsonElement> merged, int catalogVersion)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("CatalogVersion", catalogVersion);
            foreach (var (key, value) in merged.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Every JSON array EVER shipped for a key (generated once from the resource's git
    /// history, canonical System.Text.Json form). When the workspace value matches one of
    /// these, it was never customized and the merge refreshes it to the current embedded
    /// value; keys missing here have only ever shipped one value, so any difference is a
    /// user customization.
    /// PATTERN: when you change an embedded selector and bump EmbeddedCatalogVersion, add
    /// the OUTGOING value to this key's list (and to the embedded resource first, of course).
    /// </summary>
    private static readonly Dictionary<string, string[][]> ShippedValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DeepThinkToggle"] =
        [
            [".ds-toggle-button:has-text(\"DeepThink\")", "[role=\"button\"][aria-pressed]:has-text(\"DeepThink\")"],
            [".ds-toggle-button:has(span)", "[role=\"button\"][aria-pressed]", ".ds-toggle-button:has-text(\"DeepThink\")"]
        ],
        ["MessageCopyButton"] =
        [
            [".ds-message [role=\"button\"][aria-label=\"Copy\"]", ".ds-message .ds-flex [role=\"button\"]:first-child", ".ds-message [role=\"button\"]:has-text(\"Copy\")"]
        ],
        ["MessageItems"] =
        [
            [".ds-virtual-list [data-virtual-list-item-key]", ".ds-message"]
        ],
        ["SearchToggle"] =
        [
            [".ds-toggle-button:has-text(\"Search\")", "[role=\"button\"][aria-pressed]:has-text(\"Search\")"],
            [".ds-toggle-button:has(span)", "[role=\"button\"][aria-pressed]", ".ds-toggle-button:has-text(\"Search\")"]
        ],
        ["SendButton"] =
        [
            ["div:has(> input[type=\"file\"]) [role=\"button\"].ds-button--filled", "[role=\"button\"].ds-button--filled.ds-button--circle", "button[type=\"submit\"]"],
            ["div:has(> textarea) ~ * [role=\"button\"].ds-button--icon", "[role=\"button\"].ds-button--iconLabelPrimary", "[role=\"button\"].ds-button--filled.ds-button--circle", "div:has(> input[type=\"file\"]) [role=\"button\"].ds-button--filled", "button[type=\"submit\"]"]
        ],
        ["StopIndicator"] =
        [
            ["[role=\"button\"][aria-label=\"Stop\"]", "[role=\"button\"]:has-text(\"Stop\")", ".ds-button--filled:has-text(\"Stop\")"]
        ],
    };

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
