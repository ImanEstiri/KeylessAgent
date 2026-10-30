using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Parsing;

public class ResponseFormatGuardTests
{
    private readonly ResponseFormatGuard _guard = new();

    private static LimitsOptions Limits() => new();

    [Fact]
    public void Check_ValidReply_PassesWithScript()
    {
        const string script = """
            @@say
            <<<MSG
            working on it
            MSG>>>
            @@end
            @@newfile src/a.cs
            <<<CONTENT
            class A {}
            CONTENT>>>
            @@end
            @@eof
            """;

        var outcome = _guard.Check(script, Limits());

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.IsValid.Should().BeTrue();
        outcome.Data.Script.Should().NotBeNull();
        outcome.Data.Script.Commands.Should().HaveCount(2);
        outcome.Data.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Check_InvalidReply_CollectsProblemsAndReturnsNoScript()
    {
        const string script = """
            Here you go:
            @@createfile src/a.cs
            @@end
            """;

        var outcome = _guard.Check(script, Limits());

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.IsValid.Should().BeFalse();
        outcome.Data.Script.Should().BeNull();
        outcome.Data.Problems.Select(p => p.Code).Should().Contain([
            ErrorCodes.ParseStrayText, ErrorCodes.ParseUnknownCommand, ErrorCodes.ParseMissingEof]);
    }

    [Fact]
    public void Check_TruncatedReply_FlagsTruncation()
    {
        const string script = """
            @@say
            <<<MSG
            half a message
            """;

        var result = _guard.Check(script, Limits()).Data;

        result.IsValid.Should().BeFalse();
        result.LooksTruncated.Should().BeTrue();
    }

    [Fact]
    public void Check_EmptyReply_IsInvalid()
    {
        var result = _guard.Check("", Limits()).Data;

        result.IsValid.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ReplyEmpty);
    }

    [Fact]
    public void Check_NextAndDoneInOneReply_IsRejected()
    {
        // Parse-valid: no grammar problems, so only the validator's control rules can reject it.
        // Bare @@next — its optional MSG payload is not accepted by the parser (separate defect).
        const string script = """
            @@say
            <<<MSG
            continuing
            MSG>>>
            @@end
            @@next
            @@end
            @@done
            <<<MSG
            all finished
            MSG>>>
            @@end
            @@eof
            """;

        var outcome = _guard.Check(script, Limits());

        outcome.Data.IsValid.Should().BeFalse();
        outcome.Data.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseConflictingControl);
    }

    [Fact]
    public void Check_SayOnly_IsRejected()
    {
        const string script = """
            @@say
            <<<MSG
            just a status line
            MSG>>>
            @@end
            @@eof
            """;

        var outcome = _guard.Check(script, Limits());

        outcome.Data.IsValid.Should().BeFalse();
        outcome.Data.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseSayOnly);
    }

    [Fact]
    public void Check_MkdirEscapingPath_IsRejected()
    {
        const string script = """
            @@mkdir ../escape-dir
            @@end
            @@eof
            """;

        var result = _guard.Check(script, Limits()).Data;

        result.IsValid.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseBadPath);
    }

    [Fact]
    public void Check_MoveFileEscapingToPath_IsRejected()
    {
        const string script = """
            @@movefile src/a.txt
            <<<TO
            ../outside.txt
            TO>>>
            @@end
            @@eof
            """;

        var result = _guard.Check(script, Limits()).Data;

        result.IsValid.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseBadPath);
    }

    [Fact]
    public void Check_MoveFileWithValidPaths_IsAccepted()
    {
        const string script = """
            @@movefile src/a.txt
            <<<TO
            src/b.txt
            TO>>>
            @@end
            @@eof
            """;

        var result = _guard.Check(script, Limits()).Data;

        result.IsValid.Should().BeTrue();
        result.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Check_OversizedReply_IsRejected()
    {
        var limits = new LimitsOptions { MaxResponseChars = 50 };
        var script = """
            @@say
            <<<MSG
            This message is far longer than the configured response cap of fifty characters.
            MSG>>>
            @@end
            @@eof
            """;

        var result = _guard.Check(script, limits).Data;

        result.IsValid.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.LimitResponseChars);
    }

    [Fact]
    public void Check_OversizedFilePayload_IsRejected()
    {
        var limits = new LimitsOptions { MaxFileContentChars = 10 };
        var script = """
            @@newfile src/big.txt
            <<<CONTENT
            0123456789012345678901234567890123456789
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = _guard.Check(script, limits).Data;

        result.IsValid.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.LimitFileContentChars);
    }

    [Theory]
    [InlineData("/abs/path.cs")]
    [InlineData("../outside.cs")]
    [InlineData("src/../../escape.cs")]
    [InlineData("src\\windows.cs")]
    [InlineData("C:/drive.cs")]
    public void Check_BadPaths_AreRejected(string path)
    {
        var script = $"""
            @@newfile {path}
            <<<CONTENT
            x
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = _guard.Check(script, Limits()).Data;

        result.IsValid.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Code == ErrorCodes.ParseBadPath);
    }

    [Fact]
    public void Check_ValidPathWithDotsInFileName_Passes()
    {
        var script = """
            @@newfile src/app.settings.development.json
            <<<CONTENT
            {}
            CONTENT>>>
            @@end
            @@eof
            """;

        var result = _guard.Check(script, Limits()).Data;

        result.IsValid.Should().BeTrue();
    }
}
