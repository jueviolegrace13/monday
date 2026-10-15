using System.Security.Cryptography;
using System.Text;
using Monday.Domain.Common;

namespace Monday.Domain.Projects;

public readonly record struct ApiKeyId(Guid Value)
{
    public static ApiKeyId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// An app's credential for sending data to Monday. Belongs to exactly one environment.
/// Only a hash is kept: the plain key is shown once, when issued.
/// Format: <c>mon_&lt;prefix&gt;_&lt;secret&gt;</c>; the prefix finds the key, the hash proves it.
/// </summary>
public sealed class ApiKey
{
    private const string Scheme = "mon";
    private const int PrefixBytes = 6;
    private const int SecretBytes = 32;

    private ApiKey(ApiKeyId id, EnvironmentId environmentId, string prefix, string hash, DateTimeOffset createdAt)
    {
        Id = id;
        EnvironmentId = environmentId;
        Prefix = prefix;
        Hash = hash;
        CreatedAt = createdAt;
    }

    public ApiKeyId Id { get; }

    public EnvironmentId EnvironmentId { get; }

    /// <summary>Public part of the key, safe to show in lists.</summary>
    public string Prefix { get; }

    /// <summary>SHA-256 of the full key, hex encoded.</summary>
    public string Hash { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Set when the key is rotated: it keeps working until then, so apps can switch without downtime.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || now < ExpiresAt);

    internal static (ApiKey Key, string PlainText) Issue(EnvironmentId environmentId, DateTimeOffset now)
    {
        var prefix = Base64Url(RandomNumberGenerator.GetBytes(PrefixBytes));
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        var plain = $"{Scheme}_{prefix}_{secret}";
        return (new ApiKey(ApiKeyId.New(), environmentId, prefix, HashOf(plain), now), plain);
    }

    /// <summary>Rebuilds a stored key (persistence only).</summary>
    public static ApiKey Restore(
        ApiKeyId id, EnvironmentId environmentId, string prefix, string hash,
        DateTimeOffset createdAt, DateTimeOffset? revokedAt, DateTimeOffset? expiresAt) =>
        new(id, environmentId, prefix, hash, createdAt) { RevokedAt = revokedAt, ExpiresAt = expiresAt };

    /// <summary>Reads the prefix out of a presented key, or null when it is not a Monday key.</summary>
    public static string? PrefixOf(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        var parts = presented.Split('_', 3);
        return parts.Length == 3 && parts[0] == Scheme && parts[1].Length > 0 && parts[2].Length > 0 ? parts[1] : null;
    }

    /// <summary>Constant-time check of a presented key against this one.</summary>
    public bool Matches(string presented, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(presented);
        var presentedHash = Convert.FromHexString(HashOf(presented));
        var storedHash = Convert.FromHexString(Hash);
        return CryptographicOperations.FixedTimeEquals(presentedHash, storedHash) && IsActive(now);
    }

    internal void Revoke(DateTimeOffset now)
    {
        Guard.That(RevokedAt is null, "api_key.already_revoked", "This key is already revoked.");
        RevokedAt = now;
    }

    internal void ExpireAt(DateTimeOffset when) => ExpiresAt = when;

    private static string HashOf(string plain) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '-');
}
