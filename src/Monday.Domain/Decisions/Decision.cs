using Monday.Domain.Access;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Decisions;

public readonly record struct DecisionId(Guid Value)
{
    public static DecisionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>The kinds of choice that must come to a human (the approval gates in the plan).</summary>
public enum DecisionArea
{
    Architecture,
    Stack,
    Schema,
    Dependency,
    Security,
    Deploy,
}

public enum DecisionStatus
{
    Proposed,
    Approved,
    Rejected,
}

public sealed record DecisionOption(string Key, string Title, string TradeOffs);

public sealed record DecisionProposedEvent(ProjectId ProjectId, DecisionId DecisionId, string Title, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record DecisionResolvedEvent(ProjectId ProjectId, DecisionId DecisionId, DecisionStatus Status, string? ChosenKey, UserId By, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A choice Claude brings to the Owner: two or three options with trade-offs and a
/// recommendation. Claude proposes; only an Owner resolves. Once resolved it never changes:
/// a new direction is a new decision that supersedes this one.
/// </summary>
public sealed class Decision : AggregateRoot<DecisionId>
{
    private Decision(DecisionId id, ProjectId projectId, DecisionArea area, string title, string context, IReadOnlyList<DecisionOption> options, string recommendedKey, DecisionId? supersedes, DateTimeOffset proposedAt)
        : base(id)
    {
        ProjectId = projectId;
        Area = area;
        Title = title;
        Context = context;
        Options = options;
        RecommendedKey = recommendedKey;
        Supersedes = supersedes;
        ProposedAt = proposedAt;
    }

    public ProjectId ProjectId { get; }

    public DecisionArea Area { get; }

    public string Title { get; }

    public string Context { get; }

    public IReadOnlyList<DecisionOption> Options { get; }

    public string RecommendedKey { get; }

    public DecisionId? Supersedes { get; }

    public DateTimeOffset ProposedAt { get; }

    public DecisionStatus Status { get; private set; } = DecisionStatus.Proposed;

    public string? ChosenKey { get; private set; }

    public UserId? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public string? Note { get; private set; }

    public static Decision Propose(ProjectId projectId, DecisionArea area, string title, string context, IReadOnlyList<DecisionOption> options, string recommendedKey, DateTimeOffset now, DecisionId? supersedes = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        title = Guard.NotBlank(title, "decision.title_required", "A decision needs a title", 120);
        context = Guard.NotBlank(context, "decision.context_required", "A decision needs context", 2000);
        Guard.That(options.Count is >= 2 and <= 3, "decision.option_count", "A decision offers two or three options.");
        Guard.That(options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() == options.Count, "decision.duplicate_option", "Option keys must be unique.");
        Guard.That(options.Any(o => o.Key == recommendedKey), "decision.unknown_recommendation", "The recommendation must be one of the options.");

        var decision = new Decision(DecisionId.New(), projectId, area, title, context, options, recommendedKey, supersedes, now);
        decision.Raise(new DecisionProposedEvent(projectId, decision.Id, title, now));
        return decision;
    }

    public void Approve(string optionKey, Project project, UserId by, DateTimeOffset now, string? note = null)
    {
        Resolve(project, by);
        Guard.That(Options.Any(o => o.Key == optionKey), "decision.unknown_option", "Choose one of the offered options.");
        Status = DecisionStatus.Approved;
        ChosenKey = optionKey;
        Finish(by, now, note);
    }

    public void Reject(Project project, UserId by, DateTimeOffset now, string note)
    {
        Resolve(project, by);
        Status = DecisionStatus.Rejected;
        Finish(by, now, Guard.NotBlank(note, "decision.reason_required", "Say why it was rejected", 1000));
    }

    private void Resolve(Project project, UserId by)
    {
        ArgumentNullException.ThrowIfNull(project);
        Guard.That(project.Id == ProjectId, "decision.wrong_project", "This decision belongs to another project.");
        Guard.That(Status == DecisionStatus.Proposed, "decision.already_resolved", "This decision is already resolved; propose a new one to change it.");
        project.EnsureCan(by, Permission.ApproveDecisions);
    }

    private void Finish(UserId by, DateTimeOffset now, string? note)
    {
        ResolvedBy = by;
        ResolvedAt = now;
        Note = note;
        Raise(new DecisionResolvedEvent(ProjectId, Id, Status, ChosenKey, by, now));
    }
}
