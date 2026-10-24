using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Parsing;

public class AgentScriptParserTests
{
    private readonly AgentScriptParser _parser = new();

    private AgentScriptParseResult ParseOk(string script)
    {
        var outcome = _parser.Parse(script);
        outcome.IsSuccess.Should().BeTrue("parsing itself should not fail");
        outcome.Data.IsValid.Should().BeTrue("expected a valid script, but got problems: {0}",
            string.Join("; ", outcome.Data.Problems.Select(p => p.ToDisplayString())));
        return outcome.Data;
    }

    private AgentScriptParseResult ParseBad(string script)
    {
        var outcome = _parser.Parse(script);
        outcome.IsSuccess.Should().BeTrue("parsing itself should not fail");
        outcome.Data.IsValid.Should().BeFalse("expected this script to be rejected");
        return outcome.Data;
    }

    [Fact]
    public void Parse_AllCommands_MapsEveryBlockToItsRecord()
    {
        const string script = """
            @@say
            <<<MSG
            Hello user
            MSG>>>
            @@end
            @@newfile src/Foo/Bar.cs
            <<<CONTENT
            class Bar {}
            CONTENT>>>
            @@end
            @@updatefile src/Foo/Baz.cs
            <<<CONTENT
            class Baz { updated }
            CONTENT>>>
            @@end
            @@editfile src/Foo/Qux.cs
            <<<FIND
            old text
            FIND>>>
            <<<REPLACE
            new text
            REPLACE>>>
            @@end
            @@deletefile src/Foo/Old.cs
            @@end
            @@readfile src/Foo/Bar.cs
            @@end
            @@runcommand
            <<<CMD
            dotnet build
            CMD>>>
            @@end
            @@ask
            <<<MSG
            Which database?
            MSG>>>
            @@end
            @@commit
            <<<MSG
            Add Bar and update Baz
            MSG>>>
            @@end
            @@done
            <<<MSG
            Task finished.
            MSG>>>
            @@end
            @@eof
            """;

        var result = ParseOk(script);

        result.Commands.Should().HaveCount(10);
        result.Commands.Select(c => c.Kind).Should().Equal(
            CommandKind.Say, CommandKind.NewFile, CommandKind.UpdateFile, CommandKind.EditFile,
            CommandKind.DeleteFile, CommandKind.ReadFile, CommandKind.RunCommand,
            CommandKind.Ask, CommandKind.Commit, CommandKind.Done);
        result.Commands.Select(c => c.Number).Should().BeInAscendingOrder().And.HaveCount(10);

        var say = result.Commands.OfType<SayCommand>().Single();
        say.Message.Should().Be("Hello user");

        var newFile = result.Commands.OfType<NewFileCommand>().Single();
        newFile.Path.Should().Be("src/Foo/Bar.cs");
        newFile.Content.Should().Be("class Bar {}");

        var update = result.Commands.OfType<UpdateFileCommand>().Single();
        update.Content.Should().Be("class Baz { updated }");

        var edit = result.Commands.OfType<EditFileCommand>().Single();
        edit.Path.Should().Be("src/Foo/Qux.cs");
        edit.Find.Should().Be("old text");
        edit.Replace.Should().Be("new text");

        result.Commands.OfType<DeleteFileCommand>().Single().Path.Should().Be("src/Foo/Old.cs");
        result.Commands.OfType<ReadFileCommand>().Single().Path.Should().Be("src/Foo/Bar.cs");
        result.Commands.OfType<RunCommandCommand>().Single().CommandLine.Should().Be("dotnet build");
        result.Commands.OfType<AskCommand>().Single().Question.Should().Be("Which database?");
        result.Commands.OfType<CommitCommand>().Single().Message.Should().Be("Add Bar and update Baz");
        result.Commands.OfType<DoneCommand>().Single().Summary.Should().Be("Task finished.");
    }

    [Fact]
    public void Parse_CommandNamesAreCaseInsensitive()
    {
        const string script = """
            @@NEWFILE src/a.cs
            <<<CONTENT
            x
            CONTENT>>>
            @@End
            @@EOF
            """;

        var result = ParseOk(script);
        result.Commands.Single().Should().BeOfType<NewFileCommand>();
    }

    [Fact]
    public void Parse_MultilineContent_PreservesEveryLine()
    {
        var script = """
            @@newfile src/a.txt
            <<<CONTENT
            line 1
              indented line 2

            line 4 after empty line
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = ParseOk(script);
        result.Commands.OfType<NewFileCommand>().Single().Content
            .Should().Be("line 1\n  indented line 2\n\nline 4 after empty line");
    }

    [Fact]
    public void Parse_MarkersInsideContentMidLine_DoNotEndThePayload()
    {
        var script = """
            @@newfile src/notes.md
            <<<CONTENT
            this line mentions CONTENT>>> in the middle
            and also <<<CONTENT at the start of a line would end it, but not mid-line
            CONTENT>>>
            @@end
            @@say
            <<<MSG
            MSG>>> inside a sentence
            MSG>>>
            @@end
            @@eof
            """;

        var result = ParseOk(script);
        var content = result.Commands.OfType<NewFileCommand>().Single().Content;
        content.Should().Contain("this line mentions CONTENT>>> in the middle");
        content.Should().Contain("and also <<<CONTENT at the start of a line would end it, but not mid-line");
        result.Commands.OfType<SayCommand>().Single().Message.Should().Be("MSG>>> inside a sentence");
    }

    [Fact]
    public void Parse_NonceMarkers_AreMatchedExactly()
    {
        var script = """
            @@newfile src/a.txt
            <<<CONTENT:x7
            a file that contains the line
            CONTENT>>>
            exactly
            CONTENT:x7>>>
            @@end
            @@eof
            """;

        var result = ParseOk(script);
        result.Commands.OfType<NewFileCommand>().Single().Content
            .Should().Be("a file that contains the line\nCONTENT>>>\nexactly");
    }

    [Fact]
    public void Parse_TruncatedReply_ReportsMissingEofAndTruncation()
    {
        const string script = """
            @@say
            <<<MSG
            cut off mid resp
            """;

        var result = ParseBad(script);
        result.EofSeen.Should().BeFalse();
        result.LooksTruncated.Should().BeTrue();
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseMissingEof || p.Code == ErrorCodes.ParseUnterminatedBlock);
    }

    [Fact]
    public void Parse_StrayTextOutsideBlocks_IsRejectedWithLineNumber()
    {
        const string script = """
            Sure, here is the plan:
            @@say
            <<<MSG
            hi
            MSG>>>
            @@end
            @@eof
            """;

        var result = ParseBad(script);
        result.Problems.Should().Contain(p =>
            p.Code == ErrorCodes.ParseStrayText && p.Line == 1);
    }

    [Fact]
    public void Parse_StrayTextInsideBlockButOutsidePayload_IsRejected()
    {
        const string script = """
            @@newfile src/a.cs
            some loose text
            <<<CONTENT
            x
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = ParseBad(script);
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseStrayText && p.Line == 2);
    }

    [Fact]
    public void Parse_UnknownCommand_IsRejectedWithLineNumber()
    {
        const string script = """
            @@createfile src/a.cs
            <<<CONTENT
            x
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = ParseBad(script);
        result.Problems.Should().Contain(p =>
            p.Code == ErrorCodes.ParseUnknownCommand && p.Line == 1);
    }

    [Fact]
    public void Parse_CodeFenceWrappedReply_IsTolerated()
    {
        var script = """
            ```
            @@say
            <<<MSG
            fenced
            MSG>>>
            @@end
            @@eof
            ```
            """;

        var result = ParseOk(script);
        result.Commands.OfType<SayCommand>().Single().Message.Should().Be("fenced");
    }

    [Fact]
    public void Parse_TextAfterEof_IsRejected()
    {
        const string script = """
            @@say
            <<<MSG
            hi
            MSG>>>
            @@end
            @@eof
            Hope this helps!
            """;

        var result = ParseBad(script);
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseTextAfterEof && p.Line == 7);
    }

    [Fact]
    public void Parse_MissingPayload_IsRejected()
    {
        const string script = """
            @@newfile src/a.cs
            @@end
            @@eof
            """;

        var result = ParseBad(script);
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseMissingPayload);
    }

    [Fact]
    public void Parse_MissingArgument_IsRejected()
    {
        const string script = """
            @@readfile
            @@end
            @@eof
            """;

        var result = ParseBad(script);
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseMissingArgument);
    }

    [Fact]
    public void Parse_UnexpectedPayload_IsRejected()
    {
        const string script = """
            @@deletefile src/a.cs
            <<<CONTENT
            x
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = ParseBad(script);
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseUnexpectedPayload);
    }

    [Fact]
    public void Parse_EmptyReply_IsRejected()
    {
        var result = ParseBad("   ");
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ReplyEmpty);
    }

    [Fact]
    public void Parse_MultipleProblems_AreAllReported()
    {
        const string script = """
            intro text
            @@createfile a.cs
            @@say
            <<<MSG
            hi
            MSG>>>
            @@end
            """;

        var result = ParseBad(script);
        result.Problems.Select(p => p.Code).Should().Contain([
            ErrorCodes.ParseStrayText, ErrorCodes.ParseUnknownCommand, ErrorCodes.ParseMissingEof]);
    }
}
