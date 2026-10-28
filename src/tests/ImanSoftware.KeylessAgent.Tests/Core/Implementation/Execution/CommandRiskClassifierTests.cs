using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

/// <summary>
/// Table-driven risk classifier tests: 60+ command lines covering every tier,
/// chains, quotes, path escapes, redirects, inline interpreters, and unknown programs.
/// </summary>
public class CommandRiskClassifierTests
{
    private readonly CommandRiskClassifier _classifier = new();
    private const string Ws = @"C:\ws";

    private CommandRiskTier TierOf(string cmd) => _classifier.Classify(cmd, Ws).Data.Tier;

    // ---------- ReadOnly ----------

    [Theory]
    [InlineData("git --version")]
    [InlineData("git version")]
    [InlineData("git status")]
    [InlineData("git log --oneline -5")]
    [InlineData("git diff")]
    [InlineData("git show HEAD")]
    [InlineData("git rev-parse HEAD")]
    [InlineData("git remote -v")]
    [InlineData("git ls-files")]
    [InlineData("git describe --tags")]
    [InlineData("git branch --list")]
    [InlineData("git branch --show-current")]
    [InlineData("dotnet --version")]
    [InlineData("dotnet --info")]
    [InlineData("dotnet --list-sdks")]
    [InlineData("dotnet --list-runtimes")]
    [InlineData("node -v")]
    [InlineData("node --version")]
    [InlineData("npm -v")]
    [InlineData("npm --version")]
    [InlineData("npm ls")]
    [InlineData("python --version")]
    [InlineData("python -V")]
    [InlineData("ipconfig")]
    [InlineData("ipconfig /all")]
    [InlineData("hostname")]
    [InlineData("whoami")]
    [InlineData("ver")]
    [InlineData("where git")]
    [InlineData("where dotnet")]
    [InlineData("dir")]
    [InlineData("dir /b")]
    [InlineData("ls")]
    [InlineData("ls -la")]
    [InlineData("tree")]
    [InlineData("type README.md")]
    [InlineData("cat src/Program.cs")]
    [InlineData("more notes.txt")]
    [InlineData("findstr \"test\" src/file.cs")]
    [InlineData("grep -rn \"TODO\" src/")]
    [InlineData("rg \"pattern\" src/")]
    [InlineData("echo hello")]
    public void ReadOnly_Commands_AreClassifiedAsReadOnly(string command)
    {
        TierOf(command).Should().Be(CommandRiskTier.ReadOnly, $"'{command}' is read-only");
    }

    // ---------- Build ----------

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet build -c Release")]
    [InlineData("dotnet restore")]
    [InlineData("dotnet clean")]
    public void Build_Commands_AreClassifiedAsBuild(string command)
    {
        TierOf(command).Should().Be(CommandRiskTier.Build, $"'{command}' is workspace-scoped build tooling");
    }

    // ---------- Execute ----------

    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet test --filter Category=Unit")]
    [InlineData("dotnet run")]
    [InlineData("dotnet run --project src/App")]
    [InlineData("dotnet watch")]
    [InlineData("dotnet exec bin/app.dll")]
    [InlineData("npm test")]
    [InlineData("npm run build")]
    [InlineData("npm start")]
    [InlineData("npm ci")]
    [InlineData("node src/script.js")]
    [InlineData("python src/main.py")]
    [InlineData("npx prettier --write src/")]
    public void Execute_Commands_AreClassifiedAsExecute(string command)
    {
        TierOf(command).Should().Be(CommandRiskTier.Execute, $"'{command}' runs project code or scripts");
    }

    // ---------- Risky ----------

    [Theory]
    [InlineData("python -c \"import os; os.makedirs('x')\"")]
    [InlineData("node -e \"require('fs').writeFileSync('a','b')\"")]
    [InlineData("powershell -Command \"Remove-Item -Recurse .\"")]
    [InlineData("powershell -EncodedCommand AAAA")]
    [InlineData("powershell -File script.ps1")]
    [InlineData("cmd /c del /s /q *.cs")]
    [InlineData("echo hello > output.txt")]
    [InlineData("echo hello >> output.txt")]
    [InlineData("git add -A")]
    [InlineData("git commit -m \"msg\"")]
    [InlineData("git checkout -b new-branch")]
    [InlineData("git switch main")]
    [InlineData("git reset --hard HEAD~1")]
    [InlineData("git clean -fd")]
    [InlineData("git stash")]
    [InlineData("git merge feature")]
    [InlineData("git rebase main")]
    [InlineData("git push origin main")]
    [InlineData("git config user.name test")]
    [InlineData("git -c user.name=test commit -m msg")]
    [InlineData("npm install express")]
    [InlineData("npm i lodash")]
    [InlineData("npm update")]
    [InlineData("pip install requests")]
    [InlineData("dotnet add package Serilog")]
    [InlineData("curl https://example.com")]
    [InlineData("wget https://example.com/file.zip")]
    [InlineData("del /s /q *.cs")]
    [InlineData("rm -rf src/")]
    [InlineData("rmdir /s /q build")]
    [InlineData("move file.txt dest\\")]
    [InlineData("ren old.txt new.txt")]
    [InlineData("copy a.txt b.txt")]
    [InlineData("xcopy src dest /E")]
    [InlineData("robocopy src dest /MIR")]
    [InlineData("someUnknownTool --flag")]
    [InlineData("C:\\Windows\\System32\\netstat.exe -an")]
    public void Risky_Commands_AreClassifiedAsRisky(string command)
    {
        TierOf(command).Should().Be(CommandRiskTier.Risky, $"'{command}' is risky");
    }

    // ---------- Denied ----------

    [Theory]
    [InlineData("format C:")]
    [InlineData("shutdown /s /t 0")]
    [InlineData("diskpart")]
    public void Denied_Commands_AreClassifiedAsDenied(string command)
    {
        TierOf(command).Should().Be(CommandRiskTier.Denied, $"'{command}' is on the deny list");
    }

    // ---------- Chains ----------

    [Fact]
    public void Chain_ReadOnlyAndBuild_HighestTierIsBuild()
    {
        TierOf("git --version && dotnet build").Should().Be(CommandRiskTier.Build);
    }

    [Fact]
    public void Chain_ReadOnlyAndRisky_HighestTierIsRisky()
    {
        TierOf("git status && git add -A").Should().Be(CommandRiskTier.Risky);
    }

    [Fact]
    public void Chain_AllReadOnly_IsReadOnly()
    {
        TierOf("git --version && git status && dotnet --version").Should().Be(CommandRiskTier.ReadOnly);
    }

    [Fact]
    public void Chain_WithPipe_ClassifiesPerSegment()
    {
        TierOf("type src/file.cs | findstr \"test\"").Should().Be(CommandRiskTier.ReadOnly);
    }

    // ---------- Quotes and path escapes ----------

    [Fact]
    public void PathOutsideWorkspace_IsRisky()
    {
        TierOf("type C:\\Windows\\system32\\drivers\\etc\\hosts").Should().Be(CommandRiskTier.Risky);
    }

    [Fact]
    public void PathWithDotDot_IsRisky()
    {
        TierOf("type ..\\..\\secret.txt").Should().Be(CommandRiskTier.Risky);
    }

    [Fact]
    public void PathInsideWorkspace_IsReadOnly()
    {
        TierOf("type src\\Program.cs").Should().Be(CommandRiskTier.ReadOnly);
    }

    [Fact]
    public void QuotedArgument_WithSpaces_IsTokenizedCorrectly()
    {
        TierOf("git commit -m \"this has spaces\"").Should().Be(CommandRiskTier.Risky);
    }

    [Fact]
    public void EmptyCommand_IsDenied()
    {
        TierOf("").Should().Be(CommandRiskTier.Denied);
    }
}
