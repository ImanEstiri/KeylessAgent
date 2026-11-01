using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Loads, validates and creates the per-workspace <c>keyless.json</c>.
/// </summary>
public interface IConfigurationService : IService
{
    /// <summary>
    /// Loads <c>keyless.json</c> from the workspace root. When the file is missing,
    /// the default configuration is written to it and returned. Invalid JSON or out-of-range
    /// values become precise failures, never exceptions. A file written before the current
    /// schema version is migrated: the raw file is backed up as
    /// <c>keyless.json.v{version}.bak</c> and the current defaults replace stale values.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>The effective configuration.</returns>
    Task<Outcome<KeylessOptions>> LoadAsync(string workspaceRoot);

    /// <summary>
    /// Whether the most recent <see cref="LoadAsync"/> migrated a pre-schema-version
    /// keyless.json to the current defaults. Reset by every LoadAsync call.
    /// </summary>
    bool LastLoadMigratedSchema { get; }
}
