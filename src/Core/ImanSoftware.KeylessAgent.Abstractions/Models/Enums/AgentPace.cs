namespace ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

/// <summary>
/// How many non-say commands the model may put into ONE response. The effective limit is
/// min(Pace limit, Limits.MaxCommandsPerResponse).
/// </summary>
public enum AgentPace
{
    /// <summary>One command per response — verify every result before the next step.</summary>
    Step = 0,

    /// <summary>At most 3 non-say commands per response (default).</summary>
    Balanced = 1,

    /// <summary>Up to Limits.MaxCommandsPerResponse non-say commands per response.</summary>
    Batch = 2,
}
