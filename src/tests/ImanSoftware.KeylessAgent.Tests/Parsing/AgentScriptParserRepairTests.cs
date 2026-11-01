using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Parsing;

/// <summary>
/// Repairs for the transport corruption seen in real turn logs: the chat's markdown renderer
/// collapses the model's newlines, so a payload close marker and a @@ command can arrive on
/// ONE line ("hello CONTENT>>> @@end"). The repair splits only patterns that cannot be valid
/// AgentScript as-is and leaves legitimate content untouched.
/// </summary>
public class AgentScriptParserRepairTests
{
    private readonly AgentScriptParser _parser = new();

    [Fact]
    public void Parse_InlinePayloadCloseFollowedByCommand_IsRepairedAndAccepted()
    {
        const string script = """
            @@newfile a.txt
            <<<CONTENT
            hello CONTENT>>> @@end
            @@eof
            """;

        var result = _parser.Parse(script);

        result.Data.IsValid.Should().BeTrue("the close marker and the @@ command are split onto separate lines");
        var newFile = result.Data.Commands.OfType<NewFileCommand>().Single();
        newFile.Content.Should().Be("hello");
    }

    [Fact]
    public void Parse_ManualThreeLineRepair_ParsesClean()
    {
        const string script = """
            @@newfile a.txt
            <<<CONTENT
            hello
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = _parser.Parse(script);

        foreach (var problem in result.Data.Problems)
            Console.WriteLine("###MANUAL>>> " + problem.ToDisplayString());
        result.Data.IsValid.Should().BeTrue();
        result.Data.Commands.OfType<NewFileCommand>().Single().Content.Should().Be("hello");
    }

    [Fact]
    public void Parse_LegitmateMarkerTextInsideContent_IsNotSplit()
    {
        const string script = """
            @@newfile a.txt
            <<<CONTENT
            a CONTENT>>> b
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = _parser.Parse(script);

        result.Data.IsValid.Should().BeTrue("a CONTENT>>> mention without a following @@ command is legitimate content");
        var newFile = result.Data.Commands.OfType<NewFileCommand>().Single();
        newFile.Content.Should().Contain("a CONTENT>>> b");
    }

    [Fact]
    public void RepairDirect_DumpsOutput()
    {
        var repaired = AgentScriptParser.RepairKnownCorruptions(new List<string>
        {
            "@@newfile a.txt",
            "<<<CONTENT",
            "hello CONTENT>>> @@end",
            "@@eof",
        });

        repaired.Should().Equal(
            "@@newfile a.txt",
            "<<<CONTENT",
            "hello",
            "CONTENT>>>",
            "@@end",
            "@@eof");
    }

    [Fact]
    public void RegexProbe_DumpsGroups()
    {
        var regex = new System.Text.RegularExpressions.Regex(
            "^(?<before>.*?)(?<marker>(?:CONTENT|MSG|FIND|REPLACE|CMD|TO)>>>)((?:[ \t]+@@.*))$");
        var match = regex.Match("hello CONTENT>>> @@end");
        match.Success.Should().BeTrue();
        foreach (System.Text.RegularExpressions.Group group in match.Groups)
            Console.WriteLine("###GROUP>>> " + group.Index + " '" + group.Value + "'");
    }

    [Fact]
    public void RepairDirect_ChainedCorruption_DumpsLines()
    {
        var repaired = AgentScriptParser.RepairKnownCorruptions(new List<string>
        {
            "@@newfile src/app.css",
            "<<<CONTENT",
            "body { color: black; } CONTENT>>> @@end @@newfile login.css <<<CONTENT",
            ".login { color: red; }",
            "CONTENT>>>",
            "@@end",
            "@@eof",
        });

        foreach (var line in repaired)
            Console.WriteLine("###REPAIR>>> " + line);
    }

    [Fact]
    public void Repair_SplitsEndChainedWithCommand()
    {
        var repaired = AgentScriptParser.RepairKnownCorruptions(new List<string>
        {
            "@@end @@newfile src/x.cs",
        });

        repaired.Should().Equal("@@end", "@@newfile src/x.cs");
    }
}
