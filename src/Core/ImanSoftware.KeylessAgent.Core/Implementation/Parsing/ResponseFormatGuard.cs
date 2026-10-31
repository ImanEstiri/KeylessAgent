using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Parsing;

/// <summary>
/// The single gate in front of execution: parse + validate, and produce the verdict
/// the agent loop acts on. Nothing from an invalid reply is ever executed.
/// </summary>
internal sealed class ResponseFormatGuard : IResponseFormatGuard
{
    private readonly IAgentScriptParser _parser;
    private readonly IAgentScriptValidator _validator;

    public ResponseFormatGuard()
        : this(new AgentScriptParser(), new AgentScriptValidator())
    {
    }

    internal ResponseFormatGuard(IAgentScriptParser parser, IAgentScriptValidator validator)
    {
        _parser = parser;
        _validator = validator;
    }

    /// <inheritdoc />
    public Outcome<FormatGuardResult> Check(string reply, LimitsOptions limits)
    {
        try
        {
            var parseOutcome = _parser.Parse(reply);
            if (parseOutcome.IsFailure)
                return Outcome.Failure<FormatGuardResult>(parseOutcome.Error);
            var parse = parseOutcome.Data;

            var validationOutcome = _validator.Validate(parse, reply, limits);
            if (validationOutcome.IsFailure)
                return Outcome.Failure<FormatGuardResult>(validationOutcome.Error);
            var problems = validationOutcome.Data;

            var isValid = problems.Count == 0;
            return Outcome.Successful(new FormatGuardResult
            {
                IsValid = isValid,
                Script = isValid ? parse : null,
                Problems = problems,
                LooksTruncated = !isValid && parse.LooksTruncated,
            });
        }
        catch (Exception ex)
        {
            return Outcome.Failure<FormatGuardResult>(new OutcomeError(ex));
        }
    }
}
