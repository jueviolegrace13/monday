using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Activity;

public enum ActivityKind
{
    SessionStarted,
    Read,
    Edit,
    TestRun,
    Build,
    Error,
    DecisionProposed,
    DecisionMade,
    Commit,
    SessionEnded,
}

/// <summary>
/// One thing Claude (or another actor) did while working on a project, with the graph nodes it
/// touched. The stream is append-only: it is the record that replay plays back.
/// </summary>
public sealed record ActivityEvent
{
    public const int MaxSummaryLength = 280;

    private ActivityEvent(ProjectId projectId, string sessionId, DateTimeOffset at, ActivityKind kind, string summary, IReadOnlyList<string> nodeIds)
    {
        ProjectId = projectId;
        SessionId = sessionId;
        At = at;
        Kind = kind;
        Summary = summary;
        NodeIds = nodeIds;
    }

    public ProjectId ProjectId { get; }

    /// <summary>The working session (a Claude Code session or a chat) the event belongs to.</summary>
    public string SessionId { get; }

    public DateTimeOffset At { get; }

    public ActivityKind Kind { get; }

    /// <summary>One line, no secrets, no file contents.</summary>
    public string Summary { get; }

    /// <summary>Graph node ids this event touched; they light up in the viewer.</summary>
    public IReadOnlyList<string> NodeIds { get; }

    public static ActivityEvent Record(ProjectId projectId, string sessionId, DateTimeOffset at, ActivityKind kind, string summary, IEnumerable<string>? nodeIds = null)
    {
        sessionId = Guard.NotBlank(sessionId, "activity.session_required", "An activity event needs a session id", 128);
        summary = Guard.NotBlank(summary, "activity.summary_required", "An activity event needs a summary", MaxSummaryLength);
        var nodes = (nodeIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        Guard.That(nodes.Count <= 200, "activity.too_many_nodes", "An activity event can touch at most 200 nodes.");
        return new ActivityEvent(projectId, sessionId, at, kind, summary, nodes);
    }
}
