using ImanSoftware.DateTimeProvider;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// Persists the agent session in <c>.agent/session.json</c> through IFileStorage.
/// </summary>
internal sealed class SessionStore : ISessionStore
{
    private const string SessionFileName = @".agent\session.json";

    private readonly IFileStorage _storage;
    private readonly IDateTimeProvider _time;

    public SessionStore()
        : this(ImanFileStorageFactory.CreateStorage(), new DefaultDateTimeProvider())
    {
    }

    internal SessionStore(IFileStorage storage, IDateTimeProvider time)
    {
        _storage = storage;
        _time = time;
    }

    /// <inheritdoc />
    public async Task<Outcome<AgentSession>> LoadAsync(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, SessionFileName);
        if (!_storage.FileExists(path))
            return NoSession();

        var read = await _storage.ReadAllTextAsync(path);
        if (read.IsFailure)
            return Outcome.Failure<AgentSession>(read.Error);

        if (string.IsNullOrWhiteSpace(read.Data))
            return NoSession();

        // A corrupt session file must never crash the turn: treat it as "no session" and
        // preserve the broken file under session.json.corrupt for inspection (best effort).
        AgentSession? session;
        var corrupt = false;
        try
        {
            session = ConfigurationService.CreateSerializer().Deserialize<AgentSession>(read.Data);
            corrupt = session is null;
        }
        catch (Exception)
        {
            session = null;
            corrupt = true;
        }

        if (corrupt)
        {
            await PreserveCorruptFileAsync(path, read.Data);
            return NoSession();
        }

        return Outcome.Successful(session!);
    }

    /// <summary>Renames a broken session.json to session.json.corrupt (best effort, never throws).</summary>
    private async Task PreserveCorruptFileAsync(string path, string content)
    {
        try
        {
            if (!_storage.FileExists(path))
                return;

            await _storage.WriteAllTextAsync(path + ".corrupt", content);
            await _storage.DeleteAsync(path);
        }
        catch
        {
            // Preserving the broken file is best-effort; the session is treated as absent.
        }
    }

    private static Outcome<AgentSession> NoSession()
        => Outcome.Failure<AgentSession>(new OutcomeError(
            "No saved session yet.", ErrorCodes.SessionNotFound, Outcomes.OutcomeErrorType.NotFound));

    /// <inheritdoc />
    public async Task<Outcome> SaveAsync(string workspaceRoot, AgentSession session)
    {
        var path = Path.Combine(workspaceRoot, SessionFileName);
        var updated = session with
        {
            LastActivityUtc = _time.Now.ToUniversalTime().ToString("O"),
        };

        var mkdir = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(path)!);
        if (mkdir.IsFailure)
            return mkdir;

        var json = ConfigurationService.CreateSerializer().Serialize(updated);
        return await _storage.WriteAllTextAsync(path, json);
    }

    /// <inheritdoc />
    public Task<Outcome> ClearAsync(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, SessionFileName);
        if (!_storage.FileExists(path))
            return Task.FromResult(Outcome.Successful());

        return _storage.DeleteAsync(path);
    }
}
