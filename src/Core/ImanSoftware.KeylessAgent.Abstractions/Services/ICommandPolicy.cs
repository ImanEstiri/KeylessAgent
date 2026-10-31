using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// The external-command safety gate: a deny list nothing can pass, plus the configurable allow list
/// that decides what may run automatically in <c>AutoSafe</c> mode.
/// </summary>
public interface ICommandPolicy : IService
{
    /// <summary>
    /// Validates a whole command line. Every segment split on <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and <c>|</c>
    /// is checked against the deny list.
    /// </summary>
    /// <param name="commandLine">The command line the model wants to run.</param>
    /// <param name="options">The configured command options (allow list).</param>
    /// <returns>Success, or a <c>KA_COMMAND_DENIED</c> failure naming the offending segment.</returns>
    Outcome Validate(string commandLine, CommandsOptions options);

    /// <summary>
    /// Whether the first program of the command line is on the allow list
    /// (only meaningful for segments that already passed the deny list).
    /// </summary>
    /// <param name="commandLine">The command line.</param>
    /// <param name="options">The configured command options (allow list).</param>
    bool IsAllowlisted(string commandLine, CommandsOptions options);
}
