using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.KeylessAgent.Wpf.Services;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;

/// <summary>
/// A collapsed run of unchanged diff lines, rendered as one "… N unchanged lines" row.
/// </summary>
/// <param name="Count">How many unchanged lines are hidden.</param>
internal sealed record CollapsedRun(int Count);

/// <summary>
/// View model of the diff card for one pending file change: header with kind pill, path and
/// +/− counts; expandable unified diff body; too-large fast path. Expanded while the change
/// is pending confirmation, collapsed after the turn ends.
/// </summary>
internal sealed class DiffItem : ObservableObject, IFeedItem
{
    /// <summary>Unchanged runs longer than this are collapsed in the body.</summary>
    public const int CollapseThreshold = 8;

    /// <summary>Unchanged context lines kept on each side of a collapsed run.</summary>
    public const int CollapseContext = 3;

    private bool _isExpanded;

    /// <summary>Creates the diff card and computes the diff eagerly (cheap after the too-large fast path).</summary>
    /// <param name="change">The pending file change.</param>
    public DiffItem(FileChange change)
    {
        Change = change ?? throw new ArgumentNullException(nameof(change));
        Result = LineDiff.Compare(change.OldContent, change.NewContent);
        Rows = BuildRows(Result.Lines);
        _isExpanded = !Result.TooLarge;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>The underlying file change.</summary>
    public FileChange Change { get; }

    /// <summary>The workspace-relative path shown in the header.</summary>
    public string Path => Change.Path;

    /// <summary>The change kind (Create / Update / Edit / Delete) shown as a colored pill.</summary>
    public string Kind => Change.Kind.ToString();

    /// <summary>The computed diff.</summary>
    public DiffResult Result { get; }

    /// <summary>The body rows: <see cref="DiffLine"/>s and <see cref="CollapsedRun"/>s.</summary>
    public IReadOnlyList<object> Rows { get; }

    /// <summary>Whether the diff was too large to compute.</summary>
    public bool TooLarge => Result.TooLarge;

    /// <summary>The too-large message with the line counts of both sides.</summary>
    public string TooLargeMessage
        => $"File too large to diff ({Math.Max(Result.Removed, Result.Added)} lines) — old: {Result.Removed} · new: {Result.Added}";

    /// <summary>Whether the diff body is visible (expanded while pending confirmation).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Expands or collapses the diff body (header click).</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>Collapses the card; called for every card when the turn ends.</summary>
    public void Collapse() => IsExpanded = false;

    private static List<object> BuildRows(IReadOnlyList<DiffLine> lines)
    {
        var rows = new List<object>(lines.Count);
        var run = new List<DiffLine>();

        void FlushRun()
        {
            if (run.Count <= CollapseThreshold)
            {
                rows.AddRange(run);
            }
            else
            {
                rows.AddRange(run.Take(CollapseContext));
                rows.Add(new CollapsedRun(run.Count - (CollapseContext * 2)));
                rows.AddRange(run.Take(^CollapseContext..));
            }

            run.Clear();
        }

        foreach (var line in lines)
        {
            if (line.Kind == DiffLineKind.Unchanged)
                run.Add(line);
            else
            {
                FlushRun();
                rows.Add(line);
            }
        }

        FlushRun();
        return rows;
    }
}
