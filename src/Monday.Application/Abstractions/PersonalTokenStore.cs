using Monday.Domain.Access;
using Monday.Domain.Common;

namespace Monday.Application.Abstractions;

/// <summary>Personal access tokens. Only hashes are stored. Revoking and last-used are saved on their own so neither can undo the other.</summary>
public interface IPersonalTokenStore
{
    Task AddAsync(PersonalToken token, CancellationToken ct);

    Task<PersonalToken?> GetAsync(PersonalTokenId id, CancellationToken ct);

    /// <summary>Finds a token by its public prefix (for authentication).</summary>
    Task<PersonalToken?> FindByPrefixAsync(string prefix, CancellationToken ct);

    /// <summary>One person's tokens, newest first.</summary>
    Task<IReadOnlyList<PersonalToken>> ListForUserAsync(UserId user, CancellationToken ct);

    /// <summary>Saves the revoked time (kept if already set).</summary>
    Task SaveRevokedAsync(PersonalToken token, CancellationToken ct);

    /// <summary>Saves the last-used time (never moves it back).</summary>
    Task SaveUsedAsync(PersonalToken token, CancellationToken ct);
}
