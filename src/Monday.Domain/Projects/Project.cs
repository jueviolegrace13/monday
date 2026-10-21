using Monday.Domain.Access;
using Monday.Domain.Agent;
using Monday.Domain.Common;

namespace Monday.Domain.Projects;

public readonly record struct ProjectId(Guid Value)
{
    public static ProjectId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

public sealed record Membership(UserId UserId, Role Role);

public sealed record ProjectCreated(ProjectId ProjectId, string Name, UserId Owner, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ApiKeyIssued(ProjectId ProjectId, ApiKeyId KeyId, EnvironmentId EnvironmentId, UserId By, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record ApiKeyRevoked(ProjectId ProjectId, ApiKeyId KeyId, UserId By, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record MemberChanged(ProjectId ProjectId, UserId Member, Role? Role, UserId By, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record UserHashKeyRevealed(ProjectId ProjectId, UserId By, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record EnvironmentKindChanged(ProjectId ProjectId, EnvironmentId EnvironmentId, EnvironmentKind From, EnvironmentKind To, UserId By, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A project Monday watches: its environments, the API keys apps use to report, and who may
/// do what. Every change checks the actor's permission here, so the rules hold no matter which
/// screen or API asked.
/// </summary>
public sealed class Project : AggregateRoot<ProjectId>
{
    private static readonly TimeSpan MaxRotationOverlap = TimeSpan.FromDays(7);

    private readonly List<ProjectEnvironment> _environments = [];
    private readonly List<ApiKey> _apiKeys = [];
    private readonly Dictionary<UserId, Role> _members = [];

    private Project(ProjectId id, string name, DateTimeOffset createdAt)
        : base(id)
    {
        Name = Guard.NotBlank(name, "project.name_required", "A project needs a name", 80);
        CreatedAt = createdAt;
        UserHashKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    /// <summary>
    /// The key apps use to hash user ids before sending traffic (HMAC-SHA256). Monday holds it so a
    /// Debugger can type a user id and see that user's requests, while stored traffic never names anyone.
    /// </summary>
    public string UserHashKey { get; private set; }

    /// <summary>The same keyed hash the agent computes for a user id.</summary>
    public string HashUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        using var hmac = new System.Security.Cryptography.HMACSHA256(Convert.FromHexString(UserHashKey));
        return Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(userId.Trim())))[..32].ToLowerInvariant();
    }

    /// <summary>Hands the user-hash key to the Owner for an app's configuration. Audited.</summary>
    public string RevealUserHashKey(UserId by, DateTimeOffset now)
    {
        EnsureOwner(by);
        Raise(new UserHashKeyRevealed(Id, by, now));
        return UserHashKey;
    }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<ProjectEnvironment> Environments => _environments;

    public IReadOnlyList<ApiKey> ApiKeys => _apiKeys;

    public IReadOnlyList<Membership> Members => _members.Select(m => new Membership(m.Key, m.Value)).ToList();

    /// <summary>The model the project's chat uses, with its sealed key. Null until an Owner or Admin sets one.</summary>
    public ModelSettings? Model { get; private set; }

    public UserId? ModelUpdatedBy { get; private set; }

    public DateTimeOffset? ModelUpdatedAt { get; private set; }

    /// <summary>A new project starts with one development environment and its creator as Owner.</summary>
    public static Project Create(string name, UserId owner, DateTimeOffset now)
    {
        var project = new Project(ProjectId.New(), name, now);
        project._members[owner] = Role.Owner;
        project._environments.Add(new ProjectEnvironment(EnvironmentId.New(), "development", EnvironmentKind.Development));
        project.Raise(new ProjectCreated(project.Id, project.Name, owner, now));
        return project;
    }

    /// <summary>Rebuilds a stored project (persistence only). Raises no events.</summary>
    public static Project Restore(
        ProjectId id, string name, DateTimeOffset createdAt, string userHashKey,
        IEnumerable<(EnvironmentId Id, string Name, EnvironmentKind Kind)> environments,
        IEnumerable<ApiKey> apiKeys, IEnumerable<Membership> members)
    {
        ArgumentNullException.ThrowIfNull(environments);
        ArgumentNullException.ThrowIfNull(apiKeys);
        ArgumentNullException.ThrowIfNull(members);
        var project = new Project(id, name, createdAt) { UserHashKey = userHashKey };
        project._environments.AddRange(environments.Select(e => new ProjectEnvironment(e.Id, e.Name, e.Kind)));
        project._apiKeys.AddRange(apiKeys);
        foreach (var m in members)
        {
            project._members[m.UserId] = m.Role;
        }

        return project;
    }

    /// <summary>Puts back stored model settings (persistence only). Raises no events.</summary>
    public void RestoreModel(ModelSettings model, UserId updatedBy, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(model);
        (Model, ModelUpdatedBy, ModelUpdatedAt) = (model, updatedBy, updatedAt);
    }

    public Role? RoleOf(UserId user) => _members.TryGetValue(user, out var role) ? role : null;

    public bool Can(UserId user, Permission permission) =>
        RoleOf(user) is { } role && RolePermissions.Allows(role, permission);

    public void EnsureCan(UserId user, Permission permission)
    {
        if (!Can(user, permission))
        {
            throw new AccessDeniedException(user, permission);
        }
    }

    private void EnsureOwner(UserId user)
    {
        if (RoleOf(user) != Role.Owner)
        {
            throw new DomainException("access.owner_only", "Only an Owner can do this.");
        }
    }

    public ProjectEnvironment Environment(EnvironmentId id) =>
        _environments.FirstOrDefault(e => e.Id == id)
        ?? throw new DomainException("environment.not_found", "No such environment in this project.");

    /// <summary>Adding a non-production environment is an admin task; production is Owner-only.</summary>
    public ProjectEnvironment AddEnvironment(string name, EnvironmentKind kind, UserId by)
    {
        if (kind == EnvironmentKind.Production)
        {
            EnsureOwner(by);
        }
        else
        {
            EnsureCan(by, Permission.ManageMembersAndKeys);
        }

        Guard.That(
            _environments.TrueForAll(e => !string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)),
            "environment.duplicate",
            "This project already has an environment with that name.");
        var environment = new ProjectEnvironment(EnvironmentId.New(), name, kind);
        _environments.Add(environment);
        return environment;
    }

    /// <summary>Only the Owner can mark an environment production or move it out; the move is an audited event.</summary>
    public void ChangeEnvironmentKind(EnvironmentId id, EnvironmentKind kind, UserId by, DateTimeOffset now)
    {
        EnsureOwner(by);
        var environment = Environment(id);
        if (environment.Kind == kind)
        {
            return;
        }

        var from = environment.Kind;
        environment.ChangeKind(kind);
        Raise(new EnvironmentKindChanged(Id, id, from, kind, by, now));
    }

    /// <summary>Issues a key for one environment. The plain key is returned once and never stored.</summary>
    public string IssueApiKey(EnvironmentId environmentId, UserId by, DateTimeOffset now)
    {
        EnsureCan(by, Permission.ManageMembersAndKeys);
        _ = Environment(environmentId);
        var (key, plain) = ApiKey.Issue(environmentId, now);
        _apiKeys.Add(key);
        Raise(new ApiKeyIssued(Id, key.Id, environmentId, by, now));
        return plain;
    }

    /// <summary>Issues a replacement and lets the old key keep working for the overlap, so apps switch without downtime.</summary>
    public string RotateApiKey(ApiKeyId oldKeyId, TimeSpan overlap, UserId by, DateTimeOffset now)
    {
        EnsureCan(by, Permission.ManageMembersAndKeys);
        Guard.That(overlap >= TimeSpan.Zero && overlap <= MaxRotationOverlap, "api_key.overlap_range", "Rotation overlap must be between 0 and 7 days.");
        var old = Key(oldKeyId);
        Guard.That(old.IsActive(now), "api_key.inactive", "Only an active key can be rotated.");
        var plain = IssueApiKey(old.EnvironmentId, by, now);
        old.ExpireAt(now + overlap);
        return plain;
    }

    public void RevokeApiKey(ApiKeyId keyId, UserId by, DateTimeOffset now)
    {
        EnsureCan(by, Permission.ManageMembersAndKeys);
        Key(keyId).Revoke(now);
        Raise(new ApiKeyRevoked(Id, keyId, by, now));
    }

    /// <summary>Finds the active key and environment a presented key belongs to, or null.</summary>
    public (ApiKey Key, ProjectEnvironment Environment)? Authenticate(string presented, DateTimeOffset now)
    {
        var prefix = ApiKey.PrefixOf(presented);
        if (prefix is null)
        {
            return null;
        }

        var key = _apiKeys.FirstOrDefault(k => k.Prefix == prefix);
        return key is not null && key.Matches(presented, now) ? (key, Environment(key.EnvironmentId)) : null;
    }

    public void SetMember(UserId member, Role role, UserId by, DateTimeOffset now)
    {
        EnsureCan(by, Permission.ManageMembersAndKeys);
        Guard.That(role != Role.Owner || RoleOf(by) == Role.Owner, "member.owner_only", "Only an Owner can make someone an Owner.");
        Guard.That(RoleOf(member) != Role.Owner || RoleOf(by) == Role.Owner, "member.owner_only", "Only an Owner can change another Owner.");
        EnsureOwnerRemains(member, role);
        _members[member] = role;
        Raise(new MemberChanged(Id, member, role, by, now));
    }

    public void RemoveMember(UserId member, UserId by, DateTimeOffset now)
    {
        EnsureCan(by, Permission.ManageMembersAndKeys);
        Guard.That(RoleOf(member) != Role.Owner || RoleOf(by) == Role.Owner, "member.owner_only", "Only an Owner can remove an Owner.");
        EnsureOwnerRemains(member, null);
        if (_members.Remove(member))
        {
            Raise(new MemberChanged(Id, member, null, by, now));
        }
    }

    /// <summary>
    /// Sets the model and its sealed key. Owners and Admins only; audited without the key. A stored key
    /// is never carried to another provider or base URL, so nobody can point it at a server they choose.
    /// </summary>
    public void SetModel(ModelSettings settings, UserId by, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureCan(by, Permission.ManageMembersAndKeys);
        Guard.That(Model is null || !Model.MovesKeyElsewhere(settings), "model.key_required",
            "Enter the API key again to change the provider or base URL.");
        var keyReplaced = !string.Equals(Model?.KeyCipher, settings.KeyCipher, StringComparison.Ordinal);
        (Model, ModelUpdatedBy, ModelUpdatedAt) = (settings, by, now);
        Raise(new ModelSettingsChanged(Id, settings.Provider, settings.Model, keyReplaced, by, now));
    }

    /// <summary>Removes the model and its key; chat is off until one is set again. Audited.</summary>
    public void ClearModel(UserId by, DateTimeOffset now)
    {
        EnsureCan(by, Permission.ManageMembersAndKeys);
        if (Model is null)
        {
            return;
        }

        (Model, ModelUpdatedBy, ModelUpdatedAt) = (null, by, now);
        Raise(new ModelSettingsCleared(Id, by, now));
    }

    private void EnsureOwnerRemains(UserId member, Role? newRole)
    {
        var ownersAfter = _members.Count(m => m.Value == Role.Owner && m.Key != member) + (newRole == Role.Owner ? 1 : 0);
        Guard.That(ownersAfter > 0, "member.last_owner", "A project must keep at least one Owner.");
    }

    private ApiKey Key(ApiKeyId id) =>
        _apiKeys.FirstOrDefault(k => k.Id == id)
        ?? throw new DomainException("api_key.not_found", "No such API key in this project.");
}

public sealed class AccessDeniedException(UserId user, Permission permission)
    : DomainException("access.denied", $"{user} is not allowed to {permission} on this project.")
{
    public Permission Permission { get; } = permission;
}
