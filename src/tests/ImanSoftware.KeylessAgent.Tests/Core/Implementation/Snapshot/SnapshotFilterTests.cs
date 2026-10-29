using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Snapshot;

public class SnapshotFilterTests
{
    private static readonly string[] TreePatterns = ["bin", "obj", ".git", "node_modules", ".agent", "artifacts"];

    [Fact]
    public void FilterTreeLines_DropsExcludedFoldersAndTheirChildren()
    {
        var lines = new[]
        {
            "G:.",
            "|   .editorconfig",
            "|   README.md",
            "+---src",
            "|   \\---Core",
            "|       |   Program.cs",
            "|       +---bin",
            "|       |   \\---Debug",
            "|       |           app.dll",
            "|       +---obj",
            "|       |           project.assets.json",
            "|       \\---Model",
            "|               Model.cs",
            "\\---docs",
            "        architecture.md",
        };

        var filtered = SnapshotFilter.FilterTreeLines(lines, TreePatterns);

        filtered.Should().NotContain(l => l.Contains("bin") || l.Contains("obj"));
        filtered.Should().NotContain("app.dll");
        filtered.Should().NotContain("project.assets.json");
        filtered.Should().Contain(l => l.EndsWith("---Model", StringComparison.Ordinal));
        filtered.Should().Contain(l => l.Trim() == "architecture.md");
    }

    [Fact]
    public void FilterTreeLines_NestedSameNameFolders_AreAllExcluded()
    {
        // bin inside src inside bin inside obj: every nesting level of an excluded name goes.
        var lines = new[]
        {
            "G:.",
            "+---src",
            "|   +---bin",
            "|   |   \\---obj",
            "|   |       +---bin",
            "|   |       |       nested.dll",
            "|   |       \\---obj2",
            "|   \\---Model",
            "|           Model.cs",
        };

        var filtered = SnapshotFilter.FilterTreeLines(lines, TreePatterns);

        filtered.Should().NotContain(l => l.Contains("bin"));
        filtered.Should().NotContain(l => l.Contains("obj"));
        filtered.Should().NotContain("nested.dll");
        filtered.Should().Contain(l => l.EndsWith("---Model", StringComparison.Ordinal));
    }

    [Fact]
    public void FilterTreeLines_CaseDifferences_AreExcluded()
    {
        var lines = new[]
        {
            "G:.",
            "+---BIN",
            "|   a.dll",
            "+---Obj",
            "|   b.json",
            "\\---readme.md",
        };

        var filtered = SnapshotFilter.FilterTreeLines(lines, TreePatterns);

        filtered.Should().NotContain(l => l.Contains("a.dll") || l.Contains("b.json"),
            "folder-name matching is case-insensitive");
        filtered.Should().Contain(l => l.Contains("readme.md"),
            "readme.md is not an excluded name");
    }

    [Fact]
    public void FilterTreeLines_NearNames_AreKept()
    {
        // Exact-name matching only: bins/objects/objects.dll are NOT the excluded folders.
        var lines = new[]
        {
            "G:.",
            "+---bins",
            "|   stuff.dll",
            "+---objects",
            "|   data.json",
            "\\---bins.txt",
        };

        var filtered = SnapshotFilter.FilterTreeLines(lines, TreePatterns);

        filtered.Should().HaveCount(6, "near names are never treated as the excluded folders — everything is kept");
        filtered.Should().Contain(l => l.Contains("stuff.dll"));
        filtered.Should().Contain(l => l.Contains("data.json"));
    }

    [Fact]
    public void FilterDumpSections_DropsExcludedHeadings()
    {
        var dump = """
            ### tree-view-result

            ```
            G:.
            +---src
            ```

            ### src > Program

            ```csharp
            Console.WriteLine("hi");
            ```

            ### code-dump-result

            ```
            previous dump
            ```

            ### README

            ```markdown
            hello
            ```

            """;

        var filtered = SnapshotFilter.FilterDumpSections(dump, ["tree-view-result", "code-dump-result"]);

        filtered.Should().NotContain("previous dump");
        filtered.Should().NotContain("### tree-view-result");
        filtered.Should().NotContain("### code-dump-result");
        filtered.Should().Contain("### src > Program");
        filtered.Should().Contain("Console.WriteLine");
        filtered.Should().Contain("### README");
    }

    [Fact]
    public void ChunkDump_SmallDump_StaysOnePart()
    {
        var parts = SnapshotFilter.ChunkDump("### a\nx\n\n### b\ny\n", 1000);

        parts.Should().HaveCount(1);
    }

    [Fact]
    public void ChunkDump_OversizedDump_CutsOnlyAtSectionBoundaries()
    {
        var sections = Enumerable.Range(1, 10)
            .Select(i => $"### file{i}\ncontent {i} {new string('x', 100)}\n\n")
            .ToList();
        var dump = string.Concat(sections);

        var parts = SnapshotFilter.ChunkDump(dump, 450);

        parts.Should().HaveCountGreaterThan(1);
        foreach (var part in parts)
        {
            part.Length.Should().BeLessThanOrEqualTo(450);
            part.Should().StartWith("### file");
        }
        parts.SelectMany(p => p.Split("### ", StringSplitOptions.RemoveEmptyEntries))
            .Should().HaveCount(10);
    }

    [Fact]
    public void LastHeadingSegment_TakesFileNameAfterFolderPath()
    {
        SnapshotFilter.LastHeadingSegment("src > Core > Program").Should().Be("Program");
        SnapshotFilter.LastHeadingSegment("tree-view-result").Should().Be("tree-view-result");
    }
}
