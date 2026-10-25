using FluentAssertions;
using ImanSoftware.DateTimeProvider;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Agent;

public class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-session-" + Guid.CreateVersion7().ToString("N"));

    public SessionStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsTheSession()
    {
        var store = new SessionStore(ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider());
        var session = new AgentSession
        {
            ChatUrl = "https://chat.deepseek.com/a/abc",
            Branch = "agent/20260101-0930-task",
            Turn = 3,
            SystemPromptSent = true,
            CreatedAtUtc = "2026-01-01T09:30:00Z",
        };

        (await store.SaveAsync(_root, session)).IsSuccess.Should().BeTrue();
        var loaded = await store.LoadAsync(_root);

        loaded.IsSuccess.Should().BeTrue();
        loaded.Data.Should().NotBeNull();
        loaded.Data.ChatUrl.Should().Be(session.ChatUrl);
        loaded.Data.Branch.Should().Be(session.Branch);
        loaded.Data.Turn.Should().Be(3);
        loaded.Data.SystemPromptSent.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_NoSession_FailsWithSessionNotFound()
    {
        var store = new SessionStore(ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider());

        var loaded = await store.LoadAsync(_root);

        loaded.IsFailure.Should().BeTrue();
        loaded.Error.Code.Should().Be("KA_SESSION_NOT_FOUND");
    }

    [Fact]
    public async Task ClearAsync_RemovesTheSession()
    {
        var store = new SessionStore(ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider());
        await store.SaveAsync(_root, new AgentSession { ChatUrl = "x", Branch = "b" });

        (await store.ClearAsync(_root)).IsSuccess.Should().BeTrue();
        (await store.LoadAsync(_root)).IsFailure.Should().BeTrue();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
