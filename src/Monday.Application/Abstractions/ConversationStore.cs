using Monday.Domain.Agent;
using Monday.Domain.Common;
using Monday.Domain.Projects;

namespace Monday.Application.Abstractions;

/// <summary>A conversation in a list: no messages.</summary>
public sealed record ConversationSummary(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Each person's agent conversations. Messages are only added; saving writes the ones not yet stored.</summary>
public interface IConversationStore
{
    Task<Conversation?> GetAsync(ConversationId id, CancellationToken ct);

    /// <summary>One person's conversations on a project, most recently active first.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(ProjectId project, UserId owner, CancellationToken ct);

    Task SaveAsync(Conversation conversation, CancellationToken ct);
}
