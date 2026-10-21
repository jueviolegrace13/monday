using Monday.Domain.Agent;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Domain.Audit;

/// <summary>
/// A record of a sensitive action: viewing one user's traffic, restoring archives, changing
/// keys, members or a production flag. Append-only; the Owner can read it.
/// </summary>
public sealed record AuditEntry(ProjectId ProjectId, UserId Actor, string Action, string Detail, DateTimeOffset At)
{
    public static AuditEntry For(IDomainEvent domainEvent) => domainEvent switch
    {
        ApiKeyIssued e => new(e.ProjectId, e.By, "api_key.issued", $"key {e.KeyId} for environment {e.EnvironmentId}", e.OccurredAt),
        ApiKeyRevoked e => new(e.ProjectId, e.By, "api_key.revoked", $"key {e.KeyId}", e.OccurredAt),
        MemberChanged e => new(e.ProjectId, e.By, e.Role is null ? "member.removed" : "member.set", $"{e.Member} {e.Role}".Trim(), e.OccurredAt),
        EnvironmentKindChanged e => new(e.ProjectId, e.By, "environment.kind_changed", $"{e.EnvironmentId}: {e.From} -> {e.To}", e.OccurredAt),
        ProjectCreated e => new(e.ProjectId, e.Owner, "project.created", e.Name, e.OccurredAt),
        UserHashKeyRevealed e => new(e.ProjectId, e.By, "user_hash_key.revealed", "shown to the Owner for app configuration", e.OccurredAt),
        ModelSettingsChanged e => new(e.ProjectId, e.By, "model.settings_changed", $"{e.Provider} {e.Model}{(e.KeyReplaced ? ", new key" : string.Empty)}", e.OccurredAt),
        ModelSettingsCleared e => new(e.ProjectId, e.By, "model.settings_cleared", "model and key removed", e.OccurredAt),
        _ => throw new ArgumentOutOfRangeException(nameof(domainEvent), domainEvent.GetType().Name, "Not an audited event."),
    };

    public static bool IsAudited(IDomainEvent domainEvent) =>
        domainEvent is ApiKeyIssued or ApiKeyRevoked or MemberChanged or EnvironmentKindChanged or ProjectCreated or UserHashKeyRevealed
            or ModelSettingsChanged or ModelSettingsCleared;
}
