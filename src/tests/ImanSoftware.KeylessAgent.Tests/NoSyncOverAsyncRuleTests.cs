using FluentAssertions;

namespace ImanSoftware.KeylessAgent.Tests;

/// <summary>
/// Enforces the no-sync-over-async rule: no <c>.GetAwaiter().GetResult()</c> (and no
/// <c>.Result</c>/<c>.Wait()</c> on tasks) may appear under src/ outside the test project.
/// Tests may block; production code must not.
/// </summary>
public class NoSyncOverAsyncRuleTests
{
    [Fact]
    public void ProductionSources_ContainNoSyncOverAsync()
    {
        var sourceRoot = FindSourceRoot();
        sourceRoot.Should().NotBeNull("the test must locate the src/ directory");

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot!, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot!, file);
            if (relative.Contains("tests", StringComparison.OrdinalIgnoreCase) ||
                relative.Split(Path.DirectorySeparatorChar).Any(s =>
                    s is "bin" or "obj"))
            {
                continue; // tests may block; build output is not source
            }
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal))
                    violations.Add($"{relative}:{i + 1}: GetAwaiter().GetResult()");
                else if (line.Contains(".Result", StringComparison.Ordinal) && !line.TrimStart().StartsWith("//"))
                    violations.Add($"{relative}:{i + 1}: .Result");
                else if (line.Contains(".Wait()", StringComparison.Ordinal) && !line.TrimStart().StartsWith("//"))
                    violations.Add($"{relative}:{i + 1}: .Wait()");
            }
        }

        violations.Should().BeEmpty(
            "production code must never block on async work; found {0} violation(s)", violations.Count);
    }

    /// <summary>
    /// Walks up from the test binary until the directory containing
    /// Directory.Packages.props (the repo root) is found, then appends src.
    /// </summary>
    private static string? FindSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;

        return current is null ? null : Path.Combine(current.FullName, "src");
    }
}
