using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Services;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;

/// <summary>Marker interface of every activity feed entry; rendered by per-type DataTemplates.</summary>
public interface IFeedItem;

/// <summary>The request the user sent (echoed at the top of each turn).</summary>
public sealed record UserRequestItem(string Message) : IFeedItem;

/// <summary>A plain message the model addressed to the user (<c>say</c>).</summary>
public sealed record ModelMessageItem(string Message) : IFeedItem;

/// <summary>A background progress step with its presentation from <see cref="EventPresenter"/>.</summary>
public sealed record StepItem(string Label, string Symbol, EventKind Kind, string Detail) : IFeedItem;

/// <summary>One executed command, parsed from an <c>ExecutingCommand</c> progress event.</summary>
public sealed record CommandItem(int Number, string Kind, string Target) : IFeedItem;

/// <summary>A pending file change with its before/after content (diff in Phase 7).</summary>
public sealed record DiffItem(FileChange Change) : IFeedItem;

/// <summary>An error shown in the feed (turn failure, cancellation, unexpected exception).</summary>
public sealed record ErrorItem(string Message) : IFeedItem;

/// <summary>The end-of-turn summary card (branch, commits, stop reason, command counts).</summary>
public sealed record TurnSummaryItem(
    int Turn,
    string Branch,
    IReadOnlyList<string> Commits,
    string? Summary,
    bool CompletedByDone,
    string? StoppedReason,
    bool DryRun,
    IReadOnlyList<ChangedPath> ChangedPaths,
    int AutoApprovedCommands,
    int ConfirmedCommands,
    string DiffStat) : IFeedItem;
