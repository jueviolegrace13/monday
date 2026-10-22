using Monday.Domain.Activity;
using Monday.Domain.Audit;
using Monday.Domain.CodeGraph;
using Monday.Domain.Decisions;
using Monday.Domain.Projects;

namespace Monday.Application.Abstractions;

/// <summary>The current time. Injected so rules about expiry and retention can be tested.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public interface IProjectRepository
{
    Task<Project?> GetAsync(ProjectId id, CancellationToken ct);

    /// <summary>Finds the project owning an API key by the key's public prefix.</summary>
    Task<Project?> FindByKeyPrefixAsync(string prefix, CancellationToken ct);

    Task<IReadOnlyList<Project>> ListForMemberAsync(Domain.Common.UserId user, CancellationToken ct);

    Task SaveAsync(Project project, CancellationToken ct);
}

public interface IGraphStore
{
    Task SaveAsync(CodeGraphSnapshot snapshot, CancellationToken ct);

    Task<CodeGraphSnapshot?> LatestAsync(ProjectId project, CancellationToken ct);

    Task SaveExplanationsAsync(ProjectId project, IReadOnlyList<Explanation> explanations, CancellationToken ct);

    Task<IReadOnlyDictionary<string, Explanation>> ExplanationsAsync(ProjectId project, CancellationToken ct);
}

/// <summary>Append-only store of what happened while working on a project.</summary>
public interface IActivityStore
{
    Task AppendAsync(IReadOnlyList<ActivityEvent> events, CancellationToken ct);

    Task<IReadOnlyList<ActivityEvent>> RangeAsync(ProjectId project, DateTimeOffset since, DateTimeOffset until, CancellationToken ct);
}

public interface IDecisionRepository
{
    Task<Decision?> GetAsync(DecisionId id, CancellationToken ct);

    Task<IReadOnlyList<Decision>> ListAsync(ProjectId project, CancellationToken ct);

    Task SaveAsync(Decision decision, CancellationToken ct);
}

/// <summary>Append-only audit log of sensitive actions.</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken ct);

    Task<IReadOnlyList<AuditEntry>> ListAsync(ProjectId project, CancellationToken ct);
}

/// <summary>Tokens a project's chat used, per UTC month (<c>yyyy-MM</c>), for the monthly cap.</summary>
public interface IModelUsageStore
{
    Task AddAsync(ProjectId project, string month, Usage usage, CancellationToken ct);

    Task<Usage> MonthAsync(ProjectId project, string month, CancellationToken ct);
}

/// <summary>Refuses model base URLs that resolve inside Monday's own network (loopback, private, link-local, metadata).</summary>
public interface IModelEndpointGuard
{
    Task EnsurePublicAsync(Uri baseUrl, CancellationToken ct);
}
