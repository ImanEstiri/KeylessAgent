using FluentAssertions;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation;

/// <summary>
/// The prompt resources teach the model the command grammar; when a command is added to the
/// parser, these files must list it too — otherwise the model is told its valid commands are
/// invalid. The resources are embedded from source, so the tests read the source tree.
/// </summary>
public class DocsConsistencyTests
{
    [Fact]
    public void SystemPrompt_ListsMovefileAndNext()
    {
        var prompt = ReadResource("system-prompt.md");

        prompt.Should().Contain("@@movefile",
            "the parser accepts @@movefile; omitting it from the keyword list makes the model avoid (or invent around) a valid command");
        prompt.Should().Contain("@@next",
            "the parser accepts @@next as a control directive; omitting it makes the model think only @@done ends a batch");
    }

    [Fact]
    public void FormatCorrection_ListsMovefileAndNext()
    {
        var correction = ReadResource("format-correction.txt");

        correction.Should().Contain("@@movefile",
            "the format-correction template must list every keyword the parser accepts");
        correction.Should().Contain("@@next",
            "the format-correction template must list every keyword the parser accepts");
    }

    private static string ReadResource(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var path = Path.Combine(directory!.FullName,
                "src", "Core", "ImanSoftware.KeylessAgent.Core", "Resources", name);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate src/Core/ImanSoftware.KeylessAgent.Core/Resources/{name} above {AppContext.BaseDirectory}.");
    }
}
