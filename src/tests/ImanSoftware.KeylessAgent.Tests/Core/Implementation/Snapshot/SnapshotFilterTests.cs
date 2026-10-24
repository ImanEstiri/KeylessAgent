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
