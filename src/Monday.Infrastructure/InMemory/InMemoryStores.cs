using System.Collections.Concurrent;
using Monday.Application.Abstractions;
using Monday.Domain.Activity;
using Monday.Domain.Agent;
using Monday.Domain.Audit;
using Monday.Domain.CodeGraph;
using Monday.Domain.Common;
using Monday.Domain.Decisions;
using Monday.Domain.Projects;

namespace Monday.Infrastructure.InMemory;

// In-memory stores for development and tests. They are replaced by the Postgres store once the
// live store is wired (phase 2); the ports stay the same, so nothing above this layer changes.

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
}

public sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly ConcurrentDictionary<ProjectId, Project> _projects = new();

    public Task<Project?> GetAsync(ProjectId id, CancellationToken ct) =>
        Task.FromResult(_projects.GetValueOrDefault(id));

    public Task<Project?> FindByKeyPrefixAsync(string prefix, CancellationToken ct) =>
        Task.FromResult(_projects.Values.FirstOrDefault(p => p.ApiKeys.Any(k => k.Prefix == prefix)));

    public Task<IReadOnlyList<Project>> ListForMemberAsync(UserId user, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Project>>(_projects.Values.Where(p => p.RoleOf(user) is not null).OrderBy(p => p.Name).ToList());

    public Task SaveAsync(Project project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        _projects[project.Id] = project;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryGraphStore : IGraphStore
{
    private readonly ConcurrentDictionary<ProjectId, CodeGraphSnapshot> _latest = new();
    private readonly ConcurrentDictionary<ProjectId, ConcurrentDictionary<string, Explanation>> _explanations = new();

    public Task SaveAsync(CodeGraphSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _latest[snapshot.ProjectId] = snapshot;
        return Task.CompletedTask;
    }

    public Task<CodeGraphSnapshot?> LatestAsync(ProjectId project, CancellationToken ct) =>
        Task.FromResult(_latest.GetValueOrDefault(project));

    public Task SaveExplanationsAsync(ProjectId project, IReadOnlyList<Explanation> explanations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(explanations);
        var map = _explanations.GetOrAdd(project, _ => new ConcurrentDictionary<string, Explanation>(StringComparer.Ordinal));
        foreach (var e in explanations)
        {
            map[e.NodeId] = e;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, Explanation>> ExplanationsAsync(ProjectId project, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, Explanation>>(
            _explanations.TryGetValue(project, out var map) ? new Dictionary<string, Explanation>(map) : new Dictionary<string, Explanation>());
}

public sealed class InMemoryActivityStore : IActivityStore
{
    private readonly Lock _gate = new();
    private readonly List<ActivityEvent> _events = [];

    public Task AppendAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_gate)
        {
            _events.AddRange(events);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActivityEvent>> RangeAsync(ProjectId project, DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ActivityEvent>>(
                _events.Where(e => e.ProjectId == project && e.At >= since && e.At <= until).OrderBy(e => e.At).ToList());
        }
    }
}

public sealed class InMemoryDecisionRepository : IDecisionRepository
{
    private readonly ConcurrentDictionary<DecisionId, Decision> _decisions = new();

    public Task<Decision?> GetAsync(DecisionId id, CancellationToken ct) => Task.FromResult(_decisions.GetValueOrDefault(id));

    public Task<IReadOnlyList<Decision>> ListAsync(ProjectId project, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Decision>>(_decisions.Values.Where(d => d.ProjectId == project).OrderBy(d => d.ProposedAt).ToList());

    public Task SaveAsync(Decision decision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(decision);
        _decisions[decision.Id] = decision;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly Lock _gate = new();
    private readonly List<AuditEntry> _entries = [];

    public Task AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAsync(ProjectId project, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AuditEntry>>(_entries.Where(e => e.ProjectId == project).ToList());
        }
    }
}

public sealed class InMemoryModelUsageStore : IModelUsageStore
{
    private readonly ConcurrentDictionary<(ProjectId, string), Usage> _months = new();

    public Task AddAsync(ProjectId project, string month, Usage usage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usage);
        _months.AddOrUpdate((project, month), usage, (_, sum) => new Usage(
            (int)Math.Min(int.MaxValue, (long)sum.InputTokens + usage.InputTokens),
            (int)Math.Min(int.MaxValue, (long)sum.OutputTokens + usage.OutputTokens)));
        return Task.CompletedTask;
    }

    public Task<Usage> MonthAsync(ProjectId project, string month, CancellationToken ct) =>
        Task.FromResult(_months.GetValueOrDefault((project, month), new Usage(0, 0)));
}

/// <summary>Keeps a copy on save and hands out a copy on read, so only what was saved is ever seen, as with Postgres.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<ConversationId, Conversation> _conversations = new();

    public Task<Conversation?> GetAsync(ConversationId id, CancellationToken ct) =>
        Task.FromResult(_conversations.TryGetValue(id, out var c) ? Copy(c) : null);

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(ProjectId project, UserId owner, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConversationSummary>>(_conversations.Values
            .Where(c => c.ProjectId == project && c.Owner == owner)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationSummary(c.Id.Value, c.Title, c.CreatedAt, c.UpdatedAt))
            .ToList());

    public Task SaveAsync(Conversation conversation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        _conversations[conversation.Id] = Copy(conversation);
        return Task.CompletedTask;
    }

    private static Conversation Copy(Conversation c) =>
        Conversation.Restore(c.Id, c.ProjectId, c.Owner, c.Title, c.CreatedAt, [.. c.Messages]);
}
