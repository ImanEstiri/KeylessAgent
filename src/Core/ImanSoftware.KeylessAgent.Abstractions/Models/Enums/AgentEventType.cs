namespace ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

/// <summary>
/// The kinds of progress events the agent loop reports to the UI while a turn is running.
/// </summary>
public enum AgentEventType
{
    /// <summary>The workspace and git preconditions are being checked.</summary>
    SessionStarting,

    /// <summary>The agent is connecting to the user's Chrome.</summary>
    ConnectingToBrowser,

    /// <summary>A new chat is being opened or an existing one reused.</summary>
    OpeningChat,

    /// <summary>Attachments are being uploaded or pasted into the chat.</summary>
    UploadingAttachments,

    /// <summary>The request is being sent to DeepSeek.</summary>
    SendingMessage,

    /// <summary>Waiting for DeepSeek to finish its reply.</summary>
    WaitingForReply,

    /// <summary>The reply is being parsed and validated as AgentScript.</summary>
    ValidatingReply,

    /// <summary>The reply was invalid; asking DeepSeek to resend it.</summary>
    RequestingResend,

    /// <summary>A command is being executed on the workspace.</summary>
    ExecutingCommand,

    /// <summary>The user is being asked a question or for confirmation.</summary>
    AskingUser,

    /// <summary>The results message is being sent back to DeepSeek.</summary>
    SendingResults,

    /// <summary>The workspace snapshot bat files are being refreshed.</summary>
    RefreshingSnapshot,

    /// <summary>The agent turn has finished.</summary>
    TurnFinished,
}
