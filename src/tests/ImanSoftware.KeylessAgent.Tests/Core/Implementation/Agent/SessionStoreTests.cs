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

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsSessionNotFound()
    {
        var store = new SessionStore(ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider());
        var path = Path.Combine(_root, ".agent", "session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ broken");

        var loaded = await store.LoadAsync(_root);

        loaded.IsFailure.Should().BeTrue("a corrupt session file must not crash the turn");
        loaded.Error.Code.Should().Be("KA_SESSION_NOT_FOUND");
        File.Exists(path).Should().BeFalse("the broken file is renamed away");
        File.Exists(path + ".corrupt").Should().BeTrue("the broken content is preserved for inspection");
        (await File.ReadAllTextAsync(path + ".corrupt")).Should().Be("{ broken");
    }

    [Fact]
    public async Task LoadAsync_ValidSession_IsNeverRenamed()
    {
        var store = new SessionStore(ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider());
        var path = Path.Combine(_root, ".agent", "session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            "{ \"ChatUrl\": \"https://chat.deepseek.com/a/fake\", \"Turn\": 3 }");

        var loaded = await store.LoadAsync(_root);

        loaded.IsSuccess.Should().BeTrue();
        loaded.Data.Turn.Should().Be(3);
        File.Exists(path).Should().BeTrue();
        File.Exists(path + ".corrupt").Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
