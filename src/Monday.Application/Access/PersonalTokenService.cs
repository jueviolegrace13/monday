using Monday.Application.Abstractions;
using Monday.Application.Agent;
using Monday.Application.Projects;
using Monday.Domain.Access;
using Monday.Domain.Audit;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Access;

/// <summary>What a member sends to create a token. Null projects means all of theirs; null lifetime means 90 days.</summary>
public sealed record PersonalTokenInput(string Name, string Scope, IReadOnlyList<Guid>? Projects, int? LifetimeDays);

/// <summary>A token as the list shows it. Never the secret or its hash.</summary>
public sealed record PersonalTokenView(
    Guid Id, string Name, string Prefix, string Scope, IReadOnlyList<Guid>? Projects,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt, bool Active);

/// <summary>A new token: the view plus the plain text, which exists only in this one response.</summary>
public sealed record IssuedPersonalToken(PersonalTokenView Token, string Plain)
{
    /// <summary>Keeps the plain token out of ToString, so logging the result never writes it.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"Token = {Token}, Plain = (redacted)");
        return true;
    }
}

/// <summary>
/// Use cases for personal access tokens. A token belongs to one person, so these work for the signed-in user only;
/// the audit log is per project, so issuing and revoking write one entry for each project the token can reach.
/// </summary>
public sealed class PersonalTokenService(IPersonalTokenStore tokens, IProjectRepository projects, IAuditLog audit, IClock clock)
{
    /// <summary>More days than any token may last; keeps an absurd number a clean refusal rather than an overflow.</summary>
    private const int DaysCeiling = 100_000;

    public async Task<IssuedPersonalToken> IssueAsync(UserId user, PersonalTokenInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var member = await projects.ListForMemberAsync(user, ct).ConfigureAwait(false);

        IReadOnlyList<ProjectId>? limited = null;
        if (input.Projects is { } requested)
        {
            var ids = requested.Select(g => new ProjectId(g)).ToList();
            if (ids.Any(id => member.All(p => p.Id != id)))
            {
                throw new NotFoundException("project");
            }

            limited = ids;
        }

        var lifetime = input.LifetimeDays is { } days ? TimeSpan.FromDays(Math.Clamp(days, -1, DaysCeiling)) : (TimeSpan?)null;
        var now = clock.Now;
        var (token, plain) = PersonalToken.Issue(user, input.Name, TokenScopes.Parse(input.Scope), limited, lifetime, now);
        await tokens.AddAsync(token, ct).ConfigureAwait(false);

        await AuditAsync(token, token.Projects ?? member.Select(p => p.Id).ToList(), "token.issued", now, ct).ConfigureAwait(false);
        return new IssuedPersonalToken(ViewOf(token, now), plain);
    }

    public async Task<IReadOnlyList<PersonalTokenView>> ListAsync(UserId user, CancellationToken ct)
    {
        var now = clock.Now;
        var all = await tokens.ListForUserAsync(user, ct).ConfigureAwait(false);
        return all.Select(t => ViewOf(t, now)).ToList();
    }

    /// <summary>Another person's token is "not found", the same as one that doesn't exist.</summary>
    public async Task RevokeAsync(UserId user, PersonalTokenId id, CancellationToken ct)
    {
        var token = await tokens.GetAsync(id, ct).ConfigureAwait(false);
        if (token is null || token.UserId != user)
        {
            throw new NotFoundException("token");
        }

        var now = clock.Now;
        token.Revoke(now);
        await tokens.SaveRevokedAsync(token, ct).ConfigureAwait(false);

        var reach = token.Projects ?? (await projects.ListForMemberAsync(user, ct).ConfigureAwait(false)).Select(p => p.Id).ToList();
        await AuditAsync(token, reach, "token.revoked", now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks a presented bearer token. Null when it is malformed, unknown, wrong, revoked or expired; no reason is given.
    /// A good token has its last-used time recorded, at most once a minute.
    /// </summary>
    public async Task<PersonalToken?> AuthenticateAsync(string? presented, CancellationToken ct)
    {
        var prefix = PersonalToken.PrefixOf(presented);
        if (prefix is null)
        {
            return null;
        }

        var token = await tokens.FindByPrefixAsync(prefix, ct).ConfigureAwait(false);
        var now = clock.Now;
        if (token is null || !token.Matches(presented!, now))
        {
            return null;
        }

        if (token.Touch(now))
        {
            await tokens.SaveUsedAsync(token, ct).ConfigureAwait(false);
        }

        return token;
    }

    /// <summary>One entry per project: the detail is the token's name and prefix, never the secret.</summary>
    private async Task AuditAsync(PersonalToken token, IEnumerable<ProjectId> reach, string action, DateTimeOffset at, CancellationToken ct)
    {
        var detail = $"{token.Name} (mpat_{token.Prefix})";
        foreach (var project in reach)
        {
            await audit.AppendAsync(new AuditEntry(project, token.UserId, action, detail, at), ct).ConfigureAwait(false);
        }
    }

    private static PersonalTokenView ViewOf(PersonalToken t, DateTimeOffset now) =>
        new(t.Id.Value, t.Name, $"mpat_{t.Prefix}", t.Scope.ToWire(), t.Projects?.Select(p => p.Value).ToList(),
            t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt, t.IsActive(now));
}

/// <summary>The tool-kind view of a token's rights. Read tools need any reach; proposing needs <see cref="TokenScope.ReadPropose"/>.</summary>
public static class PersonalTokenRights
{
    /// <summary>Screen actions are chat-only, so a token never allows them.</summary>
    public static bool Allows(this PersonalToken token, ProjectId project, ToolKind kind)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.AllowsProject(project) && kind switch
        {
            ToolKind.Read => true,
            ToolKind.Proposal => token.AllowsProposals,
            _ => false,
        };
    }
}
