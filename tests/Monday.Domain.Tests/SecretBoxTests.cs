using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Monday.Infrastructure.Secrets;

namespace Monday.Domain.Tests;

/// <summary>Model keys are sealed with AES-GCM under MONDAY_SECRETS_KEY: a database leak alone reveals none.</summary>
internal static class SecretBoxTests
{
    private static string NewMasterKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmSecretBox Box(string? masterKey) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MONDAY_SECRETS_KEY"] = masterKey }).Build());

    public static void SealOpenRoundTrip()
    {
        var box = Box(NewMasterKey());
        const string plain = "sk-ant-api03-example-key-1234";

        var sealedValue = box.Seal(plain);

        Assert.True(box.Enabled, "A box with a master key is enabled.");
        Assert.True(!sealedValue.Contains(plain, StringComparison.Ordinal), "The sealed value does not hold the plain key.");
        var parts = sealedValue.Split(':');
        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal(12, Convert.FromBase64String(parts[1]).Length);
        Assert.Equal(16, Convert.FromBase64String(parts[3]).Length);
        Assert.Equal(plain, box.Open(sealedValue));
        Assert.True(box.Seal(plain) != sealedValue, "Each seal uses a fresh nonce.");
    }

    public static void TamperedCipherFailsToOpen()
    {
        var box = Box(NewMasterKey());
        var parts = box.Seal("sk-ant-api03-example-key-1234").Split(':');
        var cipher = Convert.FromBase64String(parts[2]);
        cipher[0] ^= 0x01;
        var tampered = string.Join(':', parts[0], parts[1], Convert.ToBase64String(cipher), parts[3]);

        Assert.Throws<CryptographicException>(() => box.Open(tampered));
        Assert.Throws<CryptographicException>(() => box.Open("v1:not-base64:x:y"));
        Assert.Throws<CryptographicException>(() => box.Open("v9:" + string.Join(':', parts[1..])));
        Assert.Throws<CryptographicException>(() => box.Open("garbage"));
    }

    public static void WrongMasterKeyFailsToOpen()
    {
        var sealedValue = Box(NewMasterKey()).Seal("sk-ant-api03-example-key-1234");

        Assert.Throws<CryptographicException>(() => Box(NewMasterKey()).Open(sealedValue));
    }

    public static void MissingMasterKeyDisablesTheBox()
    {
        var box = Box(null);

        Assert.True(!box.Enabled, "No master key, no box.");
        var ex = Assert.Throws<InvalidOperationException>(() => box.Seal("sk-ant-api03-example-key-1234"));
        Assert.Equal("Model keys are disabled: MONDAY_SECRETS_KEY is not set.", ex.Message);
        Assert.Throws<InvalidOperationException>(() => box.Open("v1:a:b:c"));
    }

    public static void MalformedMasterKeyIsRefusedWithoutEchoingIt()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var ex = Assert.Throws<InvalidOperationException>(() => Box(shortKey));
        Assert.True(!ex.Message.Contains(shortKey, StringComparison.Ordinal), "The error does not echo the key.");
        Assert.Throws<InvalidOperationException>(() => Box("not base64!"));
    }
}
