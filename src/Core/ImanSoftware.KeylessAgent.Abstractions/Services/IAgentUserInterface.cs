using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// The single contract every UI implements to host the agent.
/// Core never touches the console or any UI library — everything the user sees goes through this interface.
/// </summary>
public interface IAgentUserInterface
{
    /// <summary>
    /// Shows one <c>say</c> message from the model to the user.
    /// </summary>
    /// <param name="message">Plain-English message.</param>
    void Say(string message);

    /// <summary>
    /// Reports background progress (waiting for reply, executing, snapshot, …).
    /// </summary>
    /// <param name="progressEvent">What is happening right now.</param>
    void ShowProgress(AgentProgressEvent progressEvent);

    /// <summary>
    /// Asks a yes/no question and waits for the answer.
    /// </summary>
    /// <param name="request">What needs confirmation and why.</param>
    /// <returns>True when the user approved.</returns>
    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <summary>
    /// Asks an open question and waits for the answer.
    /// </summary>
    /// <param name="question">The question text.</param>
    /// <returns>The user's answer.</returns>
    Task<string> AskAsync(string question);

    /// <summary>
    /// Shows a pending file change (colored diff in Ask mode) before it is applied.
    /// </summary>
    /// <param name="change">The change about to be applied.</param>
    void ShowDiff(FileChange change);

    /// <summary>
    /// Shows a numbered choice menu and waits for the selection. Arrow keys (or digits) move,
    /// Enter confirms the highlighted option, Esc/Ctrl+C cancels. Non-interactive hosts return
    /// the default option.
    /// </summary>
    /// <param name="request">The menu (title, options, default).</param>
    /// <param name="cancellationToken">Cancellation token (a cancelled token counts as cancelled).</param>
    /// <returns>The selection result.</returns>
    Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a waiting phase (chat reply, running process, snapshot scripts) so the UI can show
    /// a spinner. The returned scope is disposed when the wait ends; any other UI call
    /// (Say, Ask, Confirm, ShowDiff, ShowProgress) stops the wait display first.
    /// Never wraps a whole turn — only actual waits.
    /// </summary>
    /// <param name="message">What is being waited for.</param>
    /// <returns>A disposable scope; dispose it when the wait ends.</returns>
    IDisposable BeginWait(string message);
}
