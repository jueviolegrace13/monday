using System.Net;
using System.Net.Sockets;
using Monday.Application.Abstractions;
using Monday.Domain.Common;

namespace Monday.Infrastructure.Models;

/// <summary>A model URL Monday refuses to call. Codes: <c>model.base_url_private</c>, <c>model.base_url_https</c>.</summary>
public sealed class UnsafeEndpointException(string code, string message) : DomainException(code, message);

/// <summary>
/// Keeps model calls out of Monday's own network. A base URL is checked when it is saved
/// (<see cref="EnsurePublicAsync(Uri, CancellationToken)"/>), and every connection checks again the
/// address it actually dials (<see cref="CreateHandler"/>), so a name that resolves somewhere public at
/// save time and inside later (DNS rebinding) is still refused.
/// </summary>
public static class SafeEndpoint
{
    /// <summary>Resolves a host name to its addresses. Injectable so tests need no DNS.</summary>
    public delegate Task<IPAddress[]> Resolver(string host, CancellationToken ct);

    private static readonly Resolver SystemDns = Dns.GetHostAddressesAsync;

    public static Task EnsurePublicAsync(Uri uri, CancellationToken ct) => EnsurePublicAsync(uri, SystemDns, ct);

    public static async Task EnsurePublicAsync(Uri uri, Resolver resolve, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new UnsafeEndpointException("model.base_url_https", "The model URL must use HTTPS.");
        }

        _ = await PublicAddressesAsync(uri.IdnHost, resolve, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A handler for model adapters: every connection re-resolves and re-checks its target, connects only
    /// to the addresses it checked, and goes direct (a proxy would make the check see the proxy, not the
    /// model host). Redirects are not followed.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(Resolver? resolve = null)
    {
        var resolver = resolve ?? SystemDns;
        return new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, resolver, ct),
        };
    }

    /// <summary>The connect step: resolve, refuse if any address is inside, then dial only those addresses.</summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, Resolver resolve, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var addresses = await PublicAddressesAsync(endpoint.Host, resolve, ct).ConfigureAwait(false);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True only for a globally routable unicast address. IPv4-mapped IPv6 is judged as the IPv4 inside it,
    /// and so are the NAT64 and 6to4 forms that embed one.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address.GetAddressBytes()),
            _ => false,
        };
    }

    private static async Task<IPAddress[]> PublicAddressesAsync(string host, Resolver resolve, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        var trimmed = host.Trim('[', ']');
        IPAddress[] addresses;
        if (IPAddress.TryParse(trimmed, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolve(trimmed, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                addresses = [];
            }
        }

        // Every address must be public: a name with one public and one inside answer is refused, since the
        // connection could land on either. The message never names the addresses.
        if (addresses.Length == 0 || !Array.TrueForAll(addresses, IsPublic))
        {
            throw new UnsafeEndpointException("model.base_url_private",
                "The model URL must resolve to a public address, not loopback, private, link-local or metadata.");
        }

        return addresses;
    }

    private static bool IsPublicV4(byte[] b) => !(
        b[0] == 0                                         // 0.0.0.0/8 "this network"
        || b[0] == 10                                     // 10/8 private
        || (b[0] == 100 && (b[1] & 0xC0) == 64)           // 100.64/10 carrier-grade NAT (also cloud metadata, e.g. 100.100.100.200)
        || b[0] == 127                                    // 127/8 loopback
        || (b[0] == 169 && b[1] == 254)                   // 169.254/16 link-local, incl. 169.254.169.254 metadata
        || (b[0] == 172 && (b[1] & 0xF0) == 16)           // 172.16/12 private
        || (b[0] == 192 && b[1] == 0 && b[2] == 0)        // 192.0.0/24 protocol assignments
        || (b[0] == 192 && b[1] == 0 && b[2] == 2)        // 192.0.2/24 documentation
        || (b[0] == 192 && b[1] == 88 && b[2] == 99)      // 192.88.99/24 6to4 relay
        || (b[0] == 192 && b[1] == 168)                   // 192.168/16 private
        || (b[0] == 198 && (b[1] & 0xFE) == 18)           // 198.18/15 benchmarking
        || (b[0] == 198 && b[1] == 51 && b[2] == 100)     // 198.51.100/24 documentation
        || (b[0] == 203 && b[1] == 0 && b[2] == 113)      // 203.0.113/24 documentation
        || b[0] >= 224);                                  // 224/4 multicast, 240/4 reserved, broadcast

    private static bool IsPublicV6(byte[] b)
    {
        // 64:ff9b::/96 NAT64 and 2002::/16 6to4 carry an IPv4 address: judge that one.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
        {
            return IsPublicV4(b[12..16]);
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicV4(b[2..6]);
        }

        return !(
            b[0] == 0x00                                  // ::/8 unspecified, loopback, IPv4-compatible
            || (b[0] == 0x01 && b[1] == 0x00)             // 100::/64 discard (kept wide: 100::/16)
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) // 2001::/32 Teredo
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) // 2001:db8::/32 documentation
            || (b[0] & 0xFE) == 0xFC                      // fc00::/7 unique local, incl. fd00:ec2::254 metadata
            || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)    // fe80::/10 link-local
            || (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0)    // fec0::/10 site-local (deprecated)
            || b[0] == 0xFF);                             // ff00::/8 multicast
    }
}

/// <summary>The save-time check, behind the application's port.</summary>
public sealed class SafeEndpointGuard : IModelEndpointGuard
{
    public Task EnsurePublicAsync(Uri baseUrl, CancellationToken ct) => SafeEndpoint.EnsurePublicAsync(baseUrl, ct);
}
