using System.ComponentModel;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

/// <summary>
/// The AgentScript v1 commands the model may use in a reply.
/// The <see cref="DescriptionAttribute"/> value is the command name as it appears in a reply.
/// </summary>
public enum CommandKind
{
    /// <summary>Prints a plain-English message to the user. Does not count toward the command limit.</summary>
    [Description("say")] Say,

    /// <summary>Creates a new file. Fails if the file already exists.</summary>
    [Description("newfile")] NewFile,

    /// <summary>Replaces the whole content of an existing file. Fails if the file does not exist.</summary>
    [Description("updatefile")] UpdateFile,

    /// <summary>Replaces one occurrence of FIND with REPLACE. Fails unless FIND matches exactly once.</summary>
    [Description("editfile")] EditFile,

    /// <summary>Deletes a file after backup and (in Ask mode) confirmation.</summary>
    [Description("deletefile")] DeleteFile,

    /// <summary>Returns the current file content to the model as that command's result.</summary>
    [Description("readfile")] ReadFile,

    /// <summary>Runs a single command line after the command policy checks every segment.</summary>
    [Description("runcommand")] RunCommand,

    /// <summary>Asks the user a question and returns the answer as that command's result.</summary>
    [Description("ask")] Ask,

    /// <summary>Stages everything, commits, and pushes the agent branch.</summary>
    [Description("commit")] Commit,

    /// <summary>Ends the agent turn with a summary message.</summary>
    [Description("done")] Done,

    /// <summary>Control directive: run this batch, send results, keep waiting for the next reply.</summary>
    [Description("next")] Next,

    /// <summary>Moves/renames a file: argument = source path, TO payload = destination path.</summary>
    [Description("movefile")] MoveFile,
}
