using System.Net;
using Monday.Infrastructure.Models;

namespace Monday.Domain.Tests;

/// <summary>Monday never sends a model key to an address inside its own network: on save and on every connection.</summary>
internal static class SafeEndpointTests
{
    private static SafeEndpoint.Resolver Resolves(params string[] addresses) =>
        (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    public static void LoopbackPrivateLinkLocalAndMetadataRefused()
    {
        string[] refused =
        [
            "127.0.0.1", "127.8.8.8", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.0.5", "169.254.169.254",
            "100.64.0.1", "100.100.100.200", "0.0.0.0", "255.255.255.255", "224.0.0.1",
            "::1", "::", "fd00::1", "fd00:ec2::254", "fe80::1", "ff02::1",
            "::ffff:10.0.0.1", "::ffff:127.0.0.1", "::ffff:169.254.169.254", "64:ff9b::a00:1", "2002:a00:1::1",
        ];
        foreach (var address in refused)
        {
            Assert.True(!SafeEndpoint.IsPublic(IPAddress.Parse(address)), $"{address} must be refused.");
        }
    }

    public static void PublicAddressAllowed()
    {
        foreach (var address in new[] { "1.1.1.1", "8.8.8.8", "172.32.0.1", "100.128.0.1", "2606:4700:4700::1111", "::ffff:1.1.1.1" })
        {
            Assert.True(SafeEndpoint.IsPublic(IPAddress.Parse(address)), $"{address} is public.");
        }

        SafeEndpoint.EnsurePublicAsync(new Uri("https://api.example.com/v1"), Resolves("1.1.1.1"), default).GetAwaiter().GetResult();
    }

    public static void HostnameResolvingInsideIsRefusedOnSave()
    {
        var ex = Assert.Throws<UnsafeEndpointException>(
            () => SafeEndpoint.EnsurePublicAsync(new Uri("https://models.internal/v1"), Resolves("1.1.1.1", "10.0.0.7"), default).GetAwaiter().GetResult(),
            "model.base_url_private");
        Assert.True(!ex.Message.Contains("10.0.0.7", StringComparison.Ordinal), "The error does not reveal internal addresses.");

        Assert.Throws<UnsafeEndpointException>(
            () => SafeEndpoint.EnsurePublicAsync(new Uri("https://169.254.169.254/latest"), Resolves(), default).GetAwaiter().GetResult(),
            "model.base_url_private");
        Assert.Throws<UnsafeEndpointException>(
            () => SafeEndpoint.EnsurePublicAsync(new Uri("https://[::1]/v1"), Resolves(), default).GetAwaiter().GetResult(),
            "model.base_url_private");
        Assert.Throws<UnsafeEndpointException>(
            () => SafeEndpoint.EnsurePublicAsync(new Uri("https://nowhere.example/v1"), Resolves(), default).GetAwaiter().GetResult(),
            "model.base_url_private");
        Assert.Throws<UnsafeEndpointException>(
            () => SafeEndpoint.EnsurePublicAsync(new Uri("http://api.example.com/v1"), Resolves("1.1.1.1"), default).GetAwaiter().GetResult(),
            "model.base_url_https");
    }

    public static void ConnectCallbackRefusesRebindToPrivate()
    {
        // The name was public when saved; at connect time DNS now answers with an inside address.
        var ex = Assert.Throws<UnsafeEndpointException>(
            () => SafeEndpoint.ConnectAsync(new DnsEndPoint("api.example.com", 443), Resolves("10.0.0.1"), default).AsTask().GetAwaiter().GetResult(),
            "model.base_url_private");
        Assert.True(ex is not null, "Refused before any socket opens.");
    }

    public static void HandlerRefusesRebindThroughHttpClient()
    {
        // The whole path an adapter takes: the handler's ConnectCallback re-checks, and no proxy hides the target.
        using var handler = SafeEndpoint.CreateHandler(Resolves("169.254.169.254"));
        Assert.True(!handler.UseProxy, "A proxy would make the callback check the proxy, not the model host.");
        Assert.True(!handler.AllowAutoRedirect, "Redirects are not followed.");
        using var client = new HttpClient(handler);

        var ex = Assert.Throws<HttpRequestException>(() => client.GetAsync(new Uri("https://api.example.com/v1/models")).GetAwaiter().GetResult());
        Assert.True(ex.InnerException is UnsafeEndpointException, $"Expected the endpoint refusal inside, got {ex.InnerException?.GetType().Name}.");
    }
}
