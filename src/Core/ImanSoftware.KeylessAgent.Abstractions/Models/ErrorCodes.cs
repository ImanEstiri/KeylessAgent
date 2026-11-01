namespace ImanSoftware.KeylessAgent.Abstractions.Models;

/// <summary>
/// The catalog of stable <c>KA_</c> error codes used in every <c>Outcome</c> failure.
/// Each code is documented in <c>docs/agentscript-spec.md</c>.
/// </summary>
public static class ErrorCodes
{
    // --- Parsing / format -------------------------------------------------

    /// <summary>A <c>@@</c> keyword is not a known AgentScript v1 command.</summary>
    public const string ParseUnknownCommand = "KA_PARSE_UNKNOWN_COMMAND";

    /// <summary>Non-whitespace text outside command blocks.</summary>
    public const string ParseStrayText = "KA_PARSE_STRAY_TEXT";

    /// <summary>The reply does not end with <c>@@eof</c>.</summary>
    public const string ParseMissingEof = "KA_PARSE_MISSING_EOF";

    /// <summary>Non-whitespace text after the final <c>@@eof</c>.</summary>
    public const string ParseTextAfterEof = "KA_PARSE_TEXT_AFTER_EOF";

    /// <summary>A block was opened but never closed with <c>@@end</c>.</summary>
    public const string ParseUnterminatedBlock = "KA_PARSE_UNTERMINATED_BLOCK";

    /// <summary>A required payload section (<c>MSG</c>, <c>CONTENT</c>, <c>FIND</c>, <c>REPLACE</c>, <c>CMD</c>) is missing.</summary>
    public const string ParseMissingPayload = "KA_PARSE_MISSING_PAYLOAD";

    /// <summary>A payload section was given to a command that does not take one.</summary>
    public const string ParseUnexpectedPayload = "KA_PARSE_UNEXPECTED_PAYLOAD";

    /// <summary>A command that needs a path or argument line did not get one.</summary>
    public const string ParseMissingArgument = "KA_PARSE_MISSING_ARGUMENT";

    /// <summary>A payload start or end marker line is malformed.</summary>
    public const string ParseBadMarker = "KA_PARSE_BAD_MARKER";

    /// <summary>An unknown command option or option value (e.g. on_error=maybe).</summary>
    public const string ParseBadOption = "KA_PARSE_BAD_OPTION";

    /// <summary>Both @@next and @@done (or another conflicting control directive) in one reply.</summary>
    public const string ParseConflictingControl = "KA_PARSE_CONFLICTING_CONTROL";

    /// <summary>A reply contains only @@say and no control directive.</summary>
    public const string ParseSayOnly = "KA_PARSE_SAY_ONLY";

    /// <summary>A path is absolute, contains <c>..</c>, or uses backslashes.</summary>
    public const string ParseBadPath = "KA_PARSE_BAD_PATH";

    /// <summary>The reply is empty or whitespace only.</summary>
    public const string ReplyEmpty = "KA_REPLY_EMPTY";

    // --- Limits -----------------------------------------------------------

    /// <summary>The reply exceeds <c>Limits.MaxResponseChars</c>.</summary>
    public const string LimitResponseChars = "KA_LIMIT_RESPONSE_CHARS";

    /// <summary>A file payload exceeds <c>Limits.MaxFileContentChars</c>.</summary>
    public const string LimitFileContentChars = "KA_LIMIT_FILE_CONTENT_CHARS";

    /// <summary>An external process exceeded its timeout.</summary>
    public const string Timeout = "KA_TIMEOUT";

    // --- Workspace execution ----------------------------------------------

    /// <summary>A resolved path falls outside the workspace or inside <c>.git</c>.</summary>
    public const string PathOutsideWorkspace = "KA_PATH_OUTSIDE";

    /// <summary>A file to read, update, edit or delete was not found.</summary>
    public const string FileNotFound = "KA_FILE_NOT_FOUND";

    /// <summary><c>newfile</c> target already exists.</summary>
    public const string FileAlreadyExists = "KA_FILE_ALREADY_EXISTS";

    /// <summary><c>editfile</c> FIND text did not match exactly once.</summary>
    public const string FindNotUnique = "KA_FIND_NOT_UNIQUE";

    /// <summary>A file operation failed at the I/O level.</summary>
    public const string FileIoFailure = "KA_FILE_IO";

    // --- Policy / git -----------------------------------------------------

    /// <summary>A command was blocked by the command policy deny list or allow list.</summary>
    public const string CommandDenied = "KA_COMMAND_DENIED";

    /// <summary>The program of a command does not exist on this machine.</summary>
    public const string CommandNotFound = "KA_COMMAND_NOT_FOUND";

    /// <summary>The user declined a command that needed confirmation.</summary>
    public const string CommandDeclined = "KA_COMMAND_DECLINED";

    /// <summary>An external command ran but returned a non-zero exit code.</summary>
    public const string CommandFailed = "KA_COMMAND_FAILED";

    /// <summary>An operation was blocked because the current branch is protected.</summary>
    public const string BranchProtected = "KA_BRANCH_PROTECTED";

    /// <summary>The workspace is not a git repository.</summary>
    public const string NotAGitRepository = "KA_NOT_A_GIT_REPOSITORY";

    /// <summary>A git command failed.</summary>
    public const string GitFailure = "KA_GIT_FAILURE";

    /// <summary>git is not installed (or not on PATH) and the workspace is not a repository.</summary>
    public const string GitNotInstalled = "KA_GIT_NOT_INSTALLED";

    /// <summary>Automatic repository initialization failed.</summary>
    public const string GitInitFailed = "KA_GIT_INIT_FAILED";

    // --- Chat / browser ---------------------------------------------------

    /// <summary>The browser could not be attached to or launched.</summary>
    public const string BrowserConnectionFailed = "KA_BROWSER_CONNECTION_FAILED";

    /// <summary>A required selector could not be resolved on the page.</summary>
    public const string SelectorNotFound = "KA_SELECTOR_NOT_FOUND";

    /// <summary>No reply arrived within the configured timeout.</summary>
    public const string ReplyTimeout = "KA_REPLY_TIMEOUT";

    /// <summary>The message could not be sent (not confirmed in the DOM, retry failed too).</summary>
    public const string SendNotConfirmed = "KA_SEND_NOT_CONFIRMED";

    /// <summary>Uploading attachments to the chat failed.</summary>
    public const string AttachmentUploadFailed = "KA_ATTACHMENT_UPLOAD_FAILED";

    /// <summary>The machine is offline: nothing was uploaded or sent.</summary>
    public const string NetworkOffline = "KA_NETWORK_OFFLINE";

    /// <summary>The composer still holds stale text or attachment chips after the reset attempt.</summary>
    public const string ComposerNotClean = "KA_COMPOSER_NOT_CLEAN";

    /// <summary>The chat hit its length limit and could not be continued.</summary>
    public const string ChatExhausted = "KA_CHAT_EXHAUSTED";
    public const string Cancelled = "KA_CANCELLED";

    // --- Configuration ----------------------------------------------------

    /// <summary><c>keyless.json</c> or <c>selectors.json</c> is not valid JSON.</summary>
    public const string ConfigurationInvalid = "KA_CONFIG_INVALID";

    /// <summary>A configuration value is out of range or inconsistent.</summary>
    public const string ConfigurationValidation = "KA_CONFIG_VALIDATION";

    /// <summary>A workspace snapshot bat file or its output is missing.</summary>
    public const string SnapshotMissing = "KA_SNAPSHOT_MISSING";

    /// <summary>A snapshot bat file run failed.</summary>
    public const string SnapshotRunFailed = "KA_SNAPSHOT_RUN_FAILED";

    /// <summary>An unexpected internal failure (bug guard); reported to the model and the user as-is.</summary>
    public const string Internal = "KA_INTERNAL";

    /// <summary>No saved session exists yet (expected on the first run).</summary>
    public const string SessionNotFound = "KA_SESSION_NOT_FOUND";
}
