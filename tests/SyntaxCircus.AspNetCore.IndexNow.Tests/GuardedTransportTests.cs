using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace SyntaxCircus.AspNetCore.IndexNow.Tests;

public sealed class GuardedTransportTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("3fff::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("ff01::1")]
    public void RejectsNonpublicAddresses(string address) =>
        Assert.False(GuardedTransport.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("100.63.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2606:4700:4700::1111")]
    public void PermitsPublicUnicastAddresses(string address) =>
        Assert.True(GuardedTransport.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task NoSocketIsOpenedIfDnsReturnsAMixOfPublicAndPrivateAddresses()
    {
        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await GuardedTransport.ConnectAsync(new DnsEndPoint("example.com", 443),
                (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8"), IPAddress.Loopback]),
                (_, _) => throw new InvalidOperationException("No socket should be opened."),
                TestContext.Current.CancellationToken));

        Assert.Contains("nonpublic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectsToValidatedIpAddressesWithoutAnotherDnsLookup()
    {
        var first = IPAddress.Parse("2001:4860:4860::8888");
        var second = IPAddress.Parse("8.8.8.8");
        List<IPEndPoint> endpoints = [];
        using var connection = await GuardedTransport.ConnectAsync(new DnsEndPoint("example.com", 443),
            (host, _) =>
            {
                Assert.Equal("example.com", host);
                return Task.FromResult<IPAddress[]>([first, second]);
            },
            (endpoint, _) =>
            {
                endpoints.Add(endpoint);
                if (endpoint.Address.Equals(first))
                {
                    throw new SocketException((int)SocketError.NetworkUnreachable);
                }

                return ValueTask.FromResult<Stream>(new MemoryStream());
            },
            TestContext.Current.CancellationToken);

        Assert.Equal([new IPEndPoint(first, 443), new IPEndPoint(second, 443)], endpoints);
    }

    [Fact]
    public async Task ChecksDnsAgainWhenOpeningANewConnection()
    {
        var dnsReads = 0;
        var dials = 0;
        var endpoint = new DnsEndPoint("example.com", 443);
        var token = TestContext.Current.CancellationToken;
        using var first = await GuardedTransport.ConnectAsync(endpoint, Resolve, Connect, token);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await GuardedTransport.ConnectAsync(endpoint, Resolve, Connect, token));

        Assert.Equal(2, dnsReads);
        Assert.Equal(1, dials);

        Task<IPAddress[]> Resolve(string _, CancellationToken token) =>
            Task.FromResult<IPAddress[]>([++dnsReads == 1 ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback]);

        ValueTask<Stream> Connect(IPEndPoint _, CancellationToken token)
        {
            dials++;
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }
    }

    [Fact]
    public void TransportDisallowsRedirectsProxiesCookiesAndUnboundedBodyDraining()
    {
        using var handler = GuardedTransport.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(0, handler.MaxResponseDrainSize);
    }

    [Fact]
    public void ResolvesTheRegisteredTypedClient()
    {
        var services = new ServiceCollection().AddSyntaxCircusIndexNow();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.IsType<IndexNowService>(provider.GetRequiredService<IIndexNowService>());
    }
}
