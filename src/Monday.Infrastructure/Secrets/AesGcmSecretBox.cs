using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Monday.Application.Abstractions;

namespace Monday.Infrastructure.Secrets;

/// <summary>
/// AES-GCM (256-bit key, 12-byte nonce, 16-byte tag) under the master key in <c>MONDAY_SECRETS_KEY</c>
/// (32 bytes, base64). Sealed values read <c>v1:nonce:cipher:tag</c>, so a later master key can be
/// introduced as v2 and old values re-sealed. Without the master key the box is off, not broken.
/// </summary>
public sealed class AesGcmSecretBox : ISecretBox
{
    public const string MasterKeyName = "MONDAY_SECRETS_KEY";
    private const string Version = "v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    // Binds every sealed value to this format, so a v1 value can't be replayed into another use of the key.
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("monday.secret.v1");

    private readonly byte[]? _key;

    public AesGcmSecretBox(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration[MasterKeyName];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        // The messages never echo the configured value.
        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{MasterKeyName} must be {KeySize} bytes, base64-encoded.");
        }

        if (key.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException($"{MasterKeyName} must be {KeySize} bytes, base64-encoded.");
        }

        _key = key;
    }

    public bool Enabled => _key is not null;

    public string Seal(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var key = KeyOrThrow();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plainBytes, cipher, tag, AssociatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }

        return string.Join(':', Version, Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
    }

    public string Open(string sealedValue)
    {
        ArgumentNullException.ThrowIfNull(sealedValue);
        var key = KeyOrThrow();
        var parts = sealedValue.Split(':');
        if (parts.Length != 4 || parts[0] != Version)
        {
            throw new CryptographicException("The sealed value is not in a known format.");
        }

        byte[] nonce, cipher, tag;
        try
        {
            (nonce, cipher, tag) = (Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2]), Convert.FromBase64String(parts[3]));
        }
        catch (FormatException)
        {
            throw new CryptographicException("The sealed value is not in a known format.");
        }

        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new CryptographicException("The sealed value is not in a known format.");
        }

        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, AssociatedData); // AuthenticationTagMismatchException is a CryptographicException
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private byte[] KeyOrThrow() =>
        _key ?? throw new InvalidOperationException($"Model keys are disabled: {MasterKeyName} is not set.");
}
