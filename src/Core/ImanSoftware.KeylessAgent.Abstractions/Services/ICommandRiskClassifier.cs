using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// The risk tier of a command (or of its most dangerous segment).
/// </summary>
public enum CommandRiskTier
{
    /// <summary>Writes nothing and touches nothing outside the workspace.</summary>
    ReadOnly,

    /// <summary>Workspace-scoped build tooling (dotnet build/restore/clean).</summary>
    Build,

    /// <summary>Runs project code or scripts (dotnet test/run, npm test/run, node file, …).</summary>
    Execute,

    /// <summary>Always confirmed (even in AutoSafe): interpreters, redirects, installs, deletes, git writes, unknown programs.</summary>
    Risky,

    /// <summary>Blocked by the deny list — never runs.</summary>
    Denied,
}

/// <summary>
/// The classification of one command line.
/// </summary>
/// <param name="Tier">The highest segment tier.</param>
/// <param name="Segments">Per-segment tiers in order.</param>
/// <param name="Reason">Human-readable justification for the tier (English).</param>
public sealed record CommandRiskAssessment(
    CommandRiskTier Tier,
    IReadOnlyList<CommandRiskTier> Segments,
    string Reason);

/// <summary>
/// Classifies a command line into risk tiers. Parsing respects quotes; each segment
/// (split on &amp;&amp;, ||, ;, |) is classified separately and the command takes the highest tier.
/// </summary>
public interface ICommandRiskClassifier : IService
{
    /// <summary>
    /// Classifies a command line against the workspace.
    /// </summary>
    /// <param name="commandLine">The raw command line.</param>
    /// <param name="workspaceRoot">The workspace root (path arguments are resolved against it).</param>
    /// <returns>The assessment; Denied assessments carry the deny reason.</returns>
    Outcome<CommandRiskAssessment> Classify(string commandLine, string workspaceRoot);
}
