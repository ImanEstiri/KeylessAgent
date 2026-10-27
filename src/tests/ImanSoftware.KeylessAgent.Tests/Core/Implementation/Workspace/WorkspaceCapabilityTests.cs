using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Workspace;

public class WorkspaceInspectorTests
{
    private static WorkspaceInspector Inspector(FakeGitService git) => new(git);

    [Fact]
    public async Task GitNotInstalled_NoGitModeCapabilities()
    {
        var git = new FakeGitService { GitInstalled = false, IsRepository = false };
        var outcome = await Inspector(git).InspectAsync("C:\\ws", new GitOptions());

        outcome.Data.NoGitMode.Should().BeTrue();
        outcome.Data.IsRepository.Should().BeFalse();
        outcome.Data.GitInstalled.Should().BeFalse();
    }

    [Fact]
    public async Task Repository_OnAgentBranch_IsDetected()
    {
        var git = new FakeGitService { CurrentBranch = "agent/2026-test", IsRepository = true };
        var outcome = await Inspector(git).InspectAsync("C:\\ws", new GitOptions());

        outcome.Data.IsRepository.Should().BeTrue();
        outcome.Data.IsAgentBranch.Should().BeTrue();
        outcome.Data.NoGitMode.Should().BeFalse();
    }

    [Fact]
    public async Task SecondCall_IsCached_NoNewProbe()
    {
        var git = new FakeGitService { CurrentBranch = "main", IsRepository = true };
        var inspector = Inspector(git);

        await inspector.InspectAsync("C:\\ws", new GitOptions());
        var probeCount = git.Calls.Count(c => c == "info");
        await inspector.InspectAsync("C:\\ws", new GitOptions());

        git.Calls.Count(c => c == "info").Should().Be(probeCount, "capabilities are cached per instance");
    }
}

public class ChangeTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-track-" + Guid.CreateVersion7().ToString("N"));
    private readonly IFileStorage _storage = ImanFileStorageFactory.CreateStorage();
    private readonly FakeGitService _git = new();
    private readonly ChangeTracker _tracker;

    public ChangeTrackerTests()
    {
        Directory.CreateDirectory(_root);
        _tracker = new ChangeTracker(new FakeGitService(), _storage);
    }

    [Fact]
    public async Task NoGit_IndexDetects_AddedModifiedDeleted()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "1");
        var baseline = (await _tracker.CaptureAsync(_root, gitAvailable: false)).Data;

        File.WriteAllText(Path.Combine(_root, "a.txt"), "22");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "new");
        var diff = await _tracker.DiffAsync(_root, baseline!, gitAvailable: false);

        diff.Data.Should().Contain(c => c.Path == "a.txt" && c.Kind == "modified");
        diff.Data.Should().Contain(c => c.Path == "b.txt" && c.Kind == "added");
    }

    [Fact]
    public async Task NoGit_Index_ExcludesIgnoredFolders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "x.dll"), "binary-ish");
        var baseline = (await _tracker.CaptureAsync(_root, gitAvailable: false)).Data;

        File.WriteAllText(Path.Combine(_root, "bin", "x.dll"), "changed");

        var diff = await _tracker.DiffAsync(_root, baseline!, gitAvailable: false);

        diff.Data.Should().NotContain(c => c.Path.StartsWith("bin/"), "ignored folders are excluded from the index");
    }

    [Fact]
    public async Task WithGit_PorcelainDiff_MapsKinds()
    {
        var git = new FakeGitService();
        var tracker = new ChangeTracker(git, _storage);
        git.PorcelainLines.AddRange([" M a.txt", "A  b.txt", "D  c.txt", "R  old.txt -> new.txt"]);
        var baseline = await tracker.CaptureAsync(_root, gitAvailable: true);
        git.PorcelainLines.Clear();
        git.PorcelainLines.AddRange(["M  a.txt", "A  b.txt", "A  new.txt"]);

        var diff = await tracker.DiffAsync(_root, baseline!.Data, gitAvailable: true);

        diff.Data.Should().NotContain(c => c.Path == "a.txt", "M in both snapshots means no change");
        diff.Data.Should().Contain(c => c.Path == "new.txt" && c.Kind == "added");
        diff.Data.Should().Contain(c => c.Path == "c.txt" && c.Kind == "deleted");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, System.IO.FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }
}
