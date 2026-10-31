using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;

/// <summary>Marker interface of every activity feed entry; rendered by per-type DataTemplates.</summary>
public interface IFeedItem;

/// <summary>A plain message the model addressed to the user (<c>say</c>).</summary>
public sealed record ModelMessageItem(string Message) : IFeedItem;

/// <summary>A background progress step reported while the turn runs.</summary>
public sealed record StepItem(AgentEventType Type, string Detail) : IFeedItem;

/// <summary>A pending file change with its before/after content (diff in Phase 7).</summary>
public sealed record DiffItem(FileChange Change) : IFeedItem;
