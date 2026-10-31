using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Agent;

/// <summary>
/// A progress event reported by the agent loop while a turn is running.
/// </summary>
/// <param name="Type">What is happening right now.</param>
/// <param name="Detail">Short human-readable detail (English).</param>
public sealed record AgentProgressEvent(AgentEventType Type, string Detail);
