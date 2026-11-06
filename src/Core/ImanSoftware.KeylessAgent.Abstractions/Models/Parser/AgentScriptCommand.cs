using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

/// <summary>
/// Base record of one parsed command block from an AgentScript v1 reply.
/// <para>
/// <see cref="Number"/> is the position of the block in its response (1, 2, 3 …), assigned by the parser.
/// The model does not number commands itself.
/// </para>
/// </summary>
public abstract record AgentScriptCommand
{
    /// <summary>
    /// The 1-based position of this command block within the response it came from.
    /// </summary>
    public int Number { get; init; }

    /// <summary>
    /// The kind of the command, matching the <c>@@</c> keyword of the block.
    /// </summary>
    public abstract CommandKind Kind { get; }

    /// <summary>
    /// A short human-readable target of the command (path, command line, or message excerpt),
    /// used in result headers sent back to the model.
    /// </summary>
    public abstract string Target { get; }
}

/// <summary>Prints <paramref name="Message"/> to the user. Does not count toward the command limit.</summary>
/// <param name="Message">Plain-English message shown in the console.</param>
public sealed record SayCommand(string Message) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.Say;

    /// <inheritdoc />
    public override string Target => Message;
}

/// <summary>Creates a new file at <paramref name="Path"/> with <paramref name="Content"/>.</summary>
/// <param name="Path">Workspace-relative path using forward slashes.</param>
/// <param name="Content">Full file content.</param>
public sealed record NewFileCommand(string Path, string Content) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.NewFile;

    /// <inheritdoc />
    public override string Target => Path;
}

/// <summary>Replaces the whole content of the existing file at <paramref name="Path"/>.</summary>
/// <param name="Path">Workspace-relative path using forward slashes.</param>
/// <param name="Content">Full new file content.</param>
public sealed record UpdateFileCommand(string Path, string Content) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.UpdateFile;

    /// <inheritdoc />
    public override string Target => Path;
}

/// <summary>Replaces the single occurrence of <paramref name="Find"/> with <paramref name="Replace"/>.</summary>
/// <param name="Path">Workspace-relative path using forward slashes.</param>
/// <param name="Find">Text that must occur exactly once in the file.</param>
/// <param name="Replace">Replacement text.</param>
public sealed record EditFileCommand(string Path, string Find, string Replace) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.EditFile;

    /// <inheritdoc />
    public override string Target => Path;
}

/// <summary>Deletes the file at <paramref name="Path"/> (after backup and, in Ask mode, confirmation).</summary>
/// <param name="Path">Workspace-relative path using forward slashes.</param>
public sealed record DeleteFileCommand(string Path) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.DeleteFile;

    /// <inheritdoc />
    public override string Target => Path;
}

/// <summary>Returns the current content of the file at <paramref name="Path"/> to the model.</summary>
/// <param name="Path">Workspace-relative path using forward slashes.</param>
public sealed record ReadFileCommand(string Path) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.ReadFile;

    /// <inheritdoc />
    public override string Target => Path;
}

/// <summary>Runs <paramref name="CommandLine"/> after the command policy checks every segment.</summary>
/// <param name="CommandLine">A single command line; segments split on &amp;&amp;, ||, ; and | are each checked.</param>
public sealed record RunCommandCommand(string CommandLine, string? OnError = null) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.RunCommand;

    /// <inheritdoc />
    public override string Target => CommandLine;
}

/// <summary>Asks the user <paramref name="Question"/> and returns the answer as this command's result.</summary>
/// <param name="Question">The question the user must answer before work continues.</param>
public sealed record AskCommand(string Question) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.Ask;

    /// <inheritdoc />
    public override string Target => Question;
}

/// <summary>Stages everything, commits with <paramref name="Message"/>, and pushes the agent branch.</summary>
/// <param name="Message">Short English commit message.</param>
public sealed record CommitCommand(string Message) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.Commit;

    /// <inheritdoc />
    public override string Target => Message;
}

/// <summary>Ends the agent turn with <paramref name="Summary"/>.</summary>
/// <param name="Summary">Summary of what was done.</param>
public sealed record DoneCommand(string Summary) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.Done;

    /// <inheritdoc />
    public override string Target => Summary;
}

/// <summary>
/// Control directive: execute this batch, send the per-command results, then keep waiting for
/// the model's next reply (the turn and chat stay open). Opposite of @@done which ends the turn.
/// </summary>
/// <param name="Message">Optional note about what the model will do with the results.</param>
public sealed record NextCommand(string Message) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.Next;

    /// <inheritdoc />
    public override string Target => Message;
}

/// <summary>Moves/renames a file (sandboxed, backed up, refuses to overwrite).</summary>
/// <param name="Path">Source path, workspace-relative.</param>
/// <param name="ToPath">Destination path, workspace-relative.</param>
/// <summary>Creates a folder (and missing parents). Idempotent: an existing folder is fine.</summary>
/// <param name="Path">Workspace-relative path using forward slashes.</param>
public sealed record MkdirCommand(string Path) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.Mkdir;

    /// <inheritdoc />
    public override string Target => Path;
}

public sealed record MoveFileCommand(string Path, string ToPath) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.MoveFile;

    /// <inheritdoc />
    public override string Target => $"{Path} -> {ToPath}";
}


/// <summary>Selects the workspace-context mode for a future message. No filesystem effect.</summary>
/// <param name="Mode">snapshot / changed / files / none (case-insensitive).</param>
/// <param name="Paths">Explicit paths for the files mode; empty otherwise.</param>
public sealed record ContextCommand(string Mode, IReadOnlyList<string> Paths) : AgentScriptCommand
{
    /// <inheritdoc />
    public override CommandKind Kind => CommandKind.Context;

    /// <inheritdoc />
    public override string Target => Mode;
}