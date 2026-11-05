using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels.Dialogs;

/// <summary>
/// One numbered row of the choice menu: `N  Label` with the optional description underneath.
/// <see cref="IsSelected"/> drives the highlight; the default option starts selected.
/// </summary>
public sealed class ChoiceRowViewModel : ObservableObject
{
    private bool _isSelected;

    /// <summary>Creates a row.</summary>
    /// <param name="number">1-based display number.</param>
    /// <param name="option">The option to display.</param>
    /// <param name="isSelected">Whether the row starts highlighted.</param>
    public ChoiceRowViewModel(int number, ChoiceOption option, bool isSelected)
    {
        Number = number;
        Option = option ?? throw new ArgumentNullException(nameof(option));
        _isSelected = isSelected;
    }

    /// <summary>The 1-based display number.</summary>
    public int Number { get; }

    /// <summary>The underlying option (id, label, description).</summary>
    public ChoiceOption Option { get; }

    /// <summary>The label text.</summary>
    public string Label => Option.Label;

    /// <summary>The optional description shown in muted text under the label.</summary>
    public string? Description => Option.Description;

    /// <summary>Whether this row is currently highlighted.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// View model of the choice menu: up/down moves the highlight, Enter confirms it,
/// digits 1 to 9 pick immediately, Esc cancels.
/// </summary>
public sealed class ChoiceDialogViewModel : ObservableObject
{
    private readonly Action<ChoiceResult> _answer;
    private int _highlightedIndex;

    /// <summary>Creates the choice menu.</summary>
    /// <param name="request">Title, options and default id.</param>
    /// <param name="answer">Called once with the selection result.</param>
    public ChoiceDialogViewModel(ChoiceRequest request, Action<ChoiceResult> answer)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        _answer = answer ?? throw new ArgumentNullException(nameof(answer));

        var rows = new List<ChoiceRowViewModel>();
        for (var i = 0; i < request.Options.Count; i++)
            rows.Add(new ChoiceRowViewModel(i + 1, request.Options[i], string.Equals(request.Options[i].Id, request.DefaultId, StringComparison.Ordinal)));
        Rows = rows;

        _highlightedIndex = rows.FindIndex(row => row.IsSelected);
        if (_highlightedIndex < 0 && rows.Count > 0)
        {
            _highlightedIndex = 0;
            rows[0].IsSelected = true;
        }

        MoveUpCommand = new RelayCommand(MoveUp);
        MoveDownCommand = new RelayCommand(MoveDown);
        ConfirmCommand = new RelayCommand(Confirm);
        CancelCommand = new RelayCommand(Cancel);
    }

    /// <summary>The original menu request.</summary>
    public ChoiceRequest Request { get; }

    /// <summary>The dialog title.</summary>
    public string Title => Request.Title;

    /// <summary>The numbered rows; the highlighted row has <see cref="ChoiceRowViewModel.IsSelected"/>.</summary>
    public IReadOnlyList<ChoiceRowViewModel> Rows { get; }

    /// <summary>The 1-based number of the highlighted row; 0 when there are no options.</summary>
    public int HighlightedNumber => Rows.Count == 0 ? 0 : _highlightedIndex + 1;

    /// <summary>Moves the highlight up (wraps nothing, clamps at the first row).</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Moves the highlight down (clamps at the last row).</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Confirms the highlighted option (Enter key).</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>Cancels the menu (Esc key) and completes with <see cref="ChoiceResult.CancelledMenu"/>.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Moves the highlight to the previous row.</summary>
    public void MoveUp()
    {
        if (_highlightedIndex > 0)
            Highlight(_highlightedIndex - 1);
    }

    /// <summary>Moves the highlight to the next row.</summary>
    public void MoveDown()
    {
        if (_highlightedIndex < Rows.Count - 1)
            Highlight(_highlightedIndex + 1);
    }

    /// <summary>Picks the option with the given 1-based number immediately; out-of-range digits are ignored.</summary>
    /// <param name="digit">The pressed digit (1 to 9).</param>
    public void PickDigit(int digit)
    {
        if (digit >= 1 && digit <= Rows.Count)
            Complete(Rows[digit - 1].Option);
    }

    /// <summary>Confirms the highlighted option.</summary>
    public void Confirm()
    {
        if (Rows.Count > 0)
            Complete(Rows[_highlightedIndex].Option);
    }

    /// <summary>Cancels the menu.</summary>
    public void Cancel() => _answer(ChoiceResult.CancelledMenu());

    private void Highlight(int index)
    {
        Rows[_highlightedIndex].IsSelected = false;
        _highlightedIndex = index;
        Rows[index].IsSelected = true;
        OnPropertyChanged(nameof(HighlightedNumber));
    }

    private void Complete(ChoiceOption option) => _answer(new ChoiceResult(option.Id, Cancelled: false));
}
