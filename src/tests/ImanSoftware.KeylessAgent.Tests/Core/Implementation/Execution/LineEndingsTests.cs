using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

public class LineEndingsTests
{
    [Fact]
    public void Detect_CrlfDominantFile_ReturnsCrlf()
    {
        LineEndings.Detect("a\r\nb\r\nc\r\n").Should().Be("\r\n");
    }

    [Fact]
    public void Detect_LfDominantFile_ReturnsLf()
    {
        LineEndings.Detect("a\nb\nc\n").Should().Be("\n");
    }

    [Fact]
    public void Detect_MixedFile_ReturnsDominantEnding()
    {
        LineEndings.Detect("a\r\nb\r\nc\nd\r\ne\r\n").Should().Be("\r\n");
        LineEndings.Detect("a\nb\nc\r\nd\n").Should().Be("\n");
    }

    [Fact]
    public void Detect_TieMeansLf()
    {
        LineEndings.Detect("a\r\nb\n").Should().Be("\n");
    }

    [Fact]
    public void NormalizeAndApply_RoundTrip()
    {
        var text = "a\r\nb\rc\nd";
        var normalized = LineEndings.NormalizeToLf(text);
        normalized.Should().Be("a\nb\nc\nd");
        LineEndings.Apply(normalized, "\r\n").Should().Be("a\r\nb\r\nc\r\nd");
    }
}

public class NewFileEndingResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-eol-" + Guid.CreateVersion7().ToString("N"));

    public NewFileEndingResolutionTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void EditorConfig_EndOfLine_IsHonored()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), "[*]\nend_of_line = lf\n");

        LineEndings.ResolveNewFileEnding(_root, Path.Combine(_root, "src", "New.cs")).Should().Be("\n");
    }

    [Fact]
    public void EditorConfig_SectionGlob_MatchesByExtension()
    {
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), "[*]\nend_of_line = crlf\n[*.sh]\nend_of_line = lf\n");

        LineEndings.ResolveNewFileEnding(_root, Path.Combine(_root, "run.sh")).Should().Be("\n");
        LineEndings.ResolveNewFileEnding(_root, Path.Combine(_root, "Program.cs")).Should().Be("\r\n");
    }

    [Fact]
    public void GitAttributes_Eol_IsHonored_WhenNoEditorConfig()
    {
        File.WriteAllText(Path.Combine(_root, ".gitattributes"), "*.md eol=lf\n");

        LineEndings.ResolveNewFileEnding(_root, Path.Combine(_root, "README.md")).Should().Be("\n");
        LineEndings.ResolveNewFileEnding(_root, Path.Combine(_root, "code.cs")).Should().Be("\r\n", "no rule matches, and Windows defaults to CRLF");
    }

    [Fact]
    public void SiblingDominant_IsUsed_WhenNoConfig()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "1\r\n2\r\n3\r\n");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "x\r\ny\r\n");

        LineEndings.ResolveNewFileEnding(_root, Path.Combine(_root, "New.cs")).Should().Be("\r\n");

        File.WriteAllText(Path.Combine(_root, "a.cs"), "1\n2\n3\n");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "x\ny\n");
        LineEndings.ResolveNewFileEnding(_root, Path.Combine(_root, "New.cs")).Should().Be("\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
