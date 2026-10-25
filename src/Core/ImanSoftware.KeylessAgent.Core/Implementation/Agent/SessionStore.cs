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

        var session = ConfigurationService.CreateSerializer().Deserialize<AgentSession>(read.Data);
        return session is null
            ? NoSession()
            : Outcome.Successful(session);
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
