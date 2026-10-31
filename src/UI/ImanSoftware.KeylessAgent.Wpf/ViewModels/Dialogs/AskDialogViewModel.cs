using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels.Dialogs;

/// <summary>
/// View model of the ask dialog: question, multiline answer box, Submit (Ctrl+Enter) and
/// Cancel (Esc, returns an empty string). The answer callback is invoked exactly once.
/// </summary>
public sealed class AskDialogViewModel : ObservableObject
{
    private readonly Action<string> _answer;
    private string _answerText = string.Empty;

    /// <summary>Creates the ask dialog.</summary>
    /// <param name="question">The question shown above the input box.</param>
    /// <param name="answer">Called once with the answer (empty when cancelled).</param>
    public AskDialogViewModel(string question, Action<string> answer)
    {
        Question = question ?? throw new ArgumentNullException(nameof(question));
        _answer = answer ?? throw new ArgumentNullException(nameof(answer));
        SubmitCommand = new RelayCommand(Submit);
        CancelCommand = new RelayCommand(() => _answer(string.Empty));
    }

    /// <summary>The question text.</summary>
    public string Question { get; }

    /// <summary>The current contents of the answer box.</summary>
    public string AnswerText
    {
        get => _answerText;
        set => SetProperty(ref _answerText, value ?? string.Empty);
    }

    /// <summary>Submits the current answer text (gold default button, Ctrl+Enter).</summary>
    public RelayCommand SubmitCommand { get; }

    /// <summary>Cancels the dialog with an empty answer (ghost button, Esc).</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Submits the current answer text.</summary>
    public void Submit() => _answer(_answerText);
}
