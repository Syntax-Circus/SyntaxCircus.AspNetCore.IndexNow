using System.Net;
using System.Net.Sockets;

namespace SyntaxCircus.AspNetCore.IndexNow;

internal static class GuardedTransport
{
    // Special-use ranges conservatively excluded using the IANA IPv4 and IPv6 registries.
    private static readonly IPNetwork[] NonpublicV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/3")
    ];

    private static readonly IPNetwork GlobalUnicastV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] NonpublicV6 =
    [
        IPNetwork.Parse("2001::/23"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3fff::/20")
    ];

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        // Disposing an unread response must not download more data in the background.
        MaxResponseDrainSize = 0,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = (context, cancellationToken) => ConnectAsync(
            context.DnsEndPoint, Dns.GetHostAddressesAsync, OpenSocketAsync, cancellationToken)
    };

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !NonpublicV4.Any(network => network.Contains(address)),
            AddressFamily.InterNetworkV6 => address.ScopeId == 0 && GlobalUnicastV6.Contains(address)
                && !NonpublicV6.Any(network => network.Contains(address)),
            _ => false
        };
    }

    internal static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect,
        CancellationToken cancellationToken)
    {
        // Connect to a validated literal, so the socket never looks up the hostname again.
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await resolve(endpoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException("The host resolved to a nonpublic address or had no public DNS records.");
        }

        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await connect(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                lastError = exception;
            }
        }

        throw new HttpRequestException("Could not connect to the host.", lastError);
    }

    private static async ValueTask<Stream> OpenSocketAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
