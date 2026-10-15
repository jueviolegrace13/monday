using Monday.Application.Abstractions;
using Monday.Domain.Access;
using Monday.Domain.Audit;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Projects;

public sealed record ProjectSummary(Guid Id, string Name, Role Role, IReadOnlyList<EnvironmentSummary> Environments);

public sealed record EnvironmentSummary(Guid Id, string Name, EnvironmentKind Kind, bool ReadOnly);

public sealed record ApiKeySummary(Guid Id, Guid EnvironmentId, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);

/// <summary>Use cases for projects, environments, members and keys, as the signed-in person.</summary>
public sealed class ProjectService(IProjectRepository projects, IAuditLog audit, IClock clock)
{
    public async Task<ProjectSummary> CreateAsync(string name, UserId owner, CancellationToken ct)
    {
        var project = Project.Create(name, owner, clock.Now);
        await SaveAsync(project, ct).ConfigureAwait(false);
        return Summarize(project, owner);
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(UserId user, CancellationToken ct)
    {
        var mine = await projects.ListForMemberAsync(user, ct).ConfigureAwait(false);
        return mine.Select(p => Summarize(p, user)).ToList();
    }

    public async Task<EnvironmentSummary> AddEnvironmentAsync(ProjectId id, string name, EnvironmentKind kind, UserId by, CancellationToken ct)
    {
        var project = await LoadForMemberAsync(id, by, ct).ConfigureAwait(false);
        var environment = project.AddEnvironment(name, kind, by);
        await SaveAsync(project, ct).ConfigureAwait(false);
        return Summarize(environment);
    }

    /// <summary>Returns the plain key once. It is never stored or shown again.</summary>
    public async Task<string> IssueKeyAsync(ProjectId id, EnvironmentId environment, UserId by, CancellationToken ct)
    {
        var project = await LoadForMemberAsync(id, by, ct).ConfigureAwait(false);
        var plain = project.IssueApiKey(environment, by, clock.Now);
        await SaveAsync(project, ct).ConfigureAwait(false);
        return plain;
    }

    public async Task<string> RotateKeyAsync(ProjectId id, ApiKeyId key, TimeSpan overlap, UserId by, CancellationToken ct)
    {
        var project = await LoadForMemberAsync(id, by, ct).ConfigureAwait(false);
        var plain = project.RotateApiKey(key, overlap, by, clock.Now);
        await SaveAsync(project, ct).ConfigureAwait(false);
        return plain;
    }

    public async Task RevokeKeyAsync(ProjectId id, ApiKeyId key, UserId by, CancellationToken ct)
    {
        var project = await LoadForMemberAsync(id, by, ct).ConfigureAwait(false);
        project.RevokeApiKey(key, by, clock.Now);
        await SaveAsync(project, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ApiKeySummary>> ListKeysAsync(ProjectId id, UserId by, CancellationToken ct)
    {
        var project = await LoadForMemberAsync(id, by, ct).ConfigureAwait(false);
        project.EnsureCan(by, Permission.ManageMembersAndKeys);
        return project.ApiKeys
            .Select(k => new ApiKeySummary(k.Id.Value, k.EnvironmentId.Value, k.Prefix, k.CreatedAt, k.ExpiresAt, k.RevokedAt))
            .ToList();
    }

    public async Task SetMemberAsync(ProjectId id, UserId member, Role role, UserId by, CancellationToken ct)
    {
        var project = await LoadForMemberAsync(id, by, ct).ConfigureAwait(false);
        project.SetMember(member, role, by, clock.Now);
        await SaveAsync(project, ct).ConfigureAwait(false);
    }

    public async Task SetEnvironmentKindAsync(ProjectId id, EnvironmentId environment, EnvironmentKind kind, UserId by, CancellationToken ct)
    {
        var project = await LoadForMemberAsync(id, by, ct).ConfigureAwait(false);
        project.ChangeEnvironmentKind(environment, kind, by, clock.Now);
        await SaveAsync(project, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a project for someone who belongs to it. Non-members get "not found", so Monday
    /// never confirms that a project they can't see exists.
    /// </summary>
    internal async Task<Project> LoadForMemberAsync(ProjectId id, UserId user, CancellationToken ct)
    {
        var project = await projects.GetAsync(id, ct).ConfigureAwait(false);
        return project is not null && project.RoleOf(user) is not null
            ? project
            : throw new NotFoundException("project");
    }

    /// <summary>Saves, then writes every audited event to the audit log.</summary>
    private async Task SaveAsync(Project project, CancellationToken ct)
    {
        await projects.SaveAsync(project, ct).ConfigureAwait(false);
        foreach (var domainEvent in project.DomainEvents.Where(AuditEntry.IsAudited))
        {
            await audit.AppendAsync(AuditEntry.For(domainEvent), ct).ConfigureAwait(false);
        }

        project.ClearDomainEvents();
    }

    private static ProjectSummary Summarize(Project project, UserId user) =>
        new(project.Id.Value, project.Name, project.RoleOf(user) ?? Role.Monitor, project.Environments.Select(Summarize).ToList());

    private static EnvironmentSummary Summarize(ProjectEnvironment e) => new(e.Id.Value, e.Name, e.Kind, e.IsReadOnly);
}

public sealed class NotFoundException(string what) : Exception($"No such {what}.")
{
    public string What { get; } = what;
}
