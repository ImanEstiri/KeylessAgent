namespace ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

/// <summary>
/// How much confirmation the agent asks for before running risky commands.
/// </summary>
public enum ApprovalMode
{
    /// <summary>Confirm every <c>runcommand</c> and <c>deletefile</c> with the user (default).</summary>
    Ask,

    /// <summary>Run allow-listed commands automatically; still ask for everything else.</summary>
    AutoSafe,

    /// <summary>Run everything automatically, with a warning shown to the user.</summary>
    Auto,
}
