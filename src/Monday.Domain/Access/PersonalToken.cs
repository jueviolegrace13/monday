using System.Security.Cryptography;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Access;

public readonly record struct PersonalTokenId(Guid Value)
{
    public static PersonalTokenId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>What a personal token may do: read only, or read and also propose a decision (never approve one).</summary>
public enum TokenScope
{
    Read,
    ReadPropose,
}

/// <summary>The scope's spelling on the wire and in storage: <c>read</c> and <c>read_propose</c>.</summary>
public static class TokenScopes
{
    public static string ToWire(this TokenScope scope) => scope switch
    {
        TokenScope.Read => "read",
        TokenScope.ReadPropose => "read_propose",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    public static TokenScope Parse(string? wire) => wire switch
    {
        "read" => TokenScope.Read,
        "read_propose" => TokenScope.ReadPropose,
        _ => throw new DomainException("token.scope_invalid", "The scope must be read or read_propose."),
    };
}

/// <summary>
/// A member's credential for an outside agent (Claude Code, Cursor) to call Monday's MCP endpoint as them.
/// Only a hash is kept: the plain token is shown once, when issued.
/// Format: <c>mpat_&lt;prefix&gt;_&lt;secret&gt;</c>; the prefix finds the token, the hash proves it.
/// </summary>
public sealed class PersonalToken
{
    public const int NameMaxLength = 60;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(365);

    /// <summary>Last-used time is recorded at most this often, so a busy agent doesn't cost a write per call.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private const string Scheme = "mpat";
    private const int PrefixBytes = 6;
    private const int SecretBytes = 32;

    private PersonalToken(
        PersonalTokenId id, UserId userId, string name, string prefix, string hash, TokenScope scope,
        IReadOnlyList<ProjectId>? projects, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Id = id;
        UserId = userId;
        Name = name;
        Prefix = prefix;
        Hash = hash;
        Scope = scope;
        Projects = projects;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public PersonalTokenId Id { get; }

    public UserId UserId { get; }

    public string Name { get; }

    /// <summary>Public part of the token, safe to show in lists.</summary>
    public string Prefix { get; }

    /// <summary>SHA-256 of the full token, hex encoded.</summary>
    public string Hash { get; }

    public TokenScope Scope { get; }

    /// <summary>The projects the token is limited to; null means every project its user is a member of.</summary>
    public IReadOnlyList<ProjectId>? Projects { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>
    /// Creates a token and its plain text. A null lifetime means 90 days; more than a year is refused.
    /// A project list must name at least one project; null means all of the user's projects.
    /// </summary>
    public static (PersonalToken Token, string Plain) Issue(
        UserId user, string name, TokenScope scope, IReadOnlyList<ProjectId>? projects, TimeSpan? lifetime, DateTimeOffset now)
    {
        var cleanName = Guard.NotBlank(name, "token.name_required", "A token name is required", NameMaxLength);
        Guard.That(Enum.IsDefined(scope), "token.scope_invalid", "The scope must be read or read_propose.");
        var span = lifetime ?? DefaultLifetime;
        Guard.That(span > TimeSpan.Zero, "token.lifetime_invalid", "The lifetime must be longer than zero.");
        Guard.That(span <= MaxLifetime, "token.lifetime_max", "A token can last one year at most.");
        Guard.That(projects is null || projects.Count > 0, "token.projects_empty", "Pick at least one project, or leave the list out for all of them.");

        var prefix = ApiKey.Base64Url(RandomNumberGenerator.GetBytes(PrefixBytes));
        var secret = ApiKey.Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var plain = $"{Scheme}_{prefix}_{secret}";
        var token = new PersonalToken(PersonalTokenId.New(), user, cleanName, prefix, ApiKey.HashOf(plain), scope,
            projects?.Distinct().ToList(), now, now + span);
        return (token, plain);
    }

    /// <summary>Rebuilds a stored token (persistence only).</summary>
    public static PersonalToken Restore(
        PersonalTokenId id, UserId userId, string name, string prefix, string hash, TokenScope scope,
        IReadOnlyList<ProjectId>? projects, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt, DateTimeOffset? lastUsedAt) =>
        new(id, userId, name, prefix, hash, scope, projects, createdAt, expiresAt) { RevokedAt = revokedAt, LastUsedAt = lastUsedAt };

    /// <summary>Reads the prefix out of a presented token, or null when it is not a personal token.</summary>
    public static string? PrefixOf(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        var parts = presented.Split('_', 3);
        return parts.Length == 3 && parts[0] == Scheme && parts[1].Length > 0 && parts[2].Length > 0 ? parts[1] : null;
    }

    /// <summary>Constant-time check of a presented token against this one. Revoked and expired tokens never match.</summary>
    public bool Matches(string presented, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(presented);
        var presentedHash = Convert.FromHexString(ApiKey.HashOf(presented));
        var storedHash = Convert.FromHexString(Hash);
        return CryptographicOperations.FixedTimeEquals(presentedHash, storedHash) && IsActive(now);
    }

    /// <summary>Whether the token may reach a project: its list when it has one, otherwise any (the user's membership still decides).</summary>
    public bool AllowsProject(ProjectId project) => Projects is null || Projects.Contains(project);

    /// <summary>Whether the token may propose a decision. Read tokens never can.</summary>
    public bool AllowsProposals => Scope == TokenScope.ReadPropose;

    public void Revoke(DateTimeOffset now)
    {
        Guard.That(RevokedAt is null, "token.already_revoked", "This token is already revoked.");
        RevokedAt = now;
    }

    /// <summary>Records a use. Returns true when the time moved (at most once a minute), so the caller knows to save it.</summary>
    public bool Touch(DateTimeOffset now)
    {
        if (LastUsedAt is { } last && now - last < TouchInterval)
        {
            return false;
        }

        LastUsedAt = now;
        return true;
    }
}
