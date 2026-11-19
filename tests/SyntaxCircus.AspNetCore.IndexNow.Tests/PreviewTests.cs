using System.Net;
using Microsoft.Extensions.Options;
using static SyntaxCircus.AspNetCore.IndexNow.Tests.IndexNowTestSite;

namespace SyntaxCircus.AspNetCore.IndexNow.Tests;

public sealed class PreviewTests
{
    [Fact]
    public async Task VerifiesThePublicKeyBeforeReadingTheSitemap()
    {
        List<Uri?> requests = [];
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            requests.Add(request.RequestUri);
            return Task.FromResult(request.RequestUri == Site.KeyLocation
                ? Text("\uFEFF" + Key + "\r\n")
                : Text(Sitemap("https://example.com/a?x=1&amp;y=2", "https://example.com/A",
                    "https://example.com/A"), "application/xml"));
        }));

        var result = await Service(client).PreviewAsync(Site, TestContext.Current.CancellationToken);

        Assert.Equal([Site.KeyLocation, Site.SitemapUrl], requests);
        Assert.Equal(["https://example.com/a?x=1&y=2", "https://example.com/A"], result.Urls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc1234")]
    [InlineData("not_an_indexnow_key")]
    [InlineData("unicode-é")]
    [InlineData("<html>not a key</html>")]
    public async Task RejectsAnInvalidPublicKeyWithoutReadingTheSitemap(string key)
    {
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            Assert.Equal(Site.KeyLocation, request.RequestUri);
            return Task.FromResult(Text(key));
        }));

        var exception = await Assert.ThrowsAsync<IndexNowException>(() =>
            Service(client).PreviewAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal("invalid_key", exception.Code);
    }

    [Fact]
    public async Task RejectsAnOversizedKeyValue()
    {
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            Assert.Equal(Site.KeyLocation, request.RequestUri);
            return Task.FromResult(Text(new string('a', 129)));
        }));

        var exception = await Assert.ThrowsAsync<IndexNowException>(() =>
            Service(client).PreviewAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal("invalid_key", exception.Code);
    }

    [Fact]
    public async Task DoesNotFollowAKeyRedirect()
    {
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            Assert.Equal(Site.KeyLocation, request.RequestUri);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://elsewhere.example/indexnow-key.txt");
            return Task.FromResult(response);
        }));

        var exception = await Assert.ThrowsAsync<IndexNowException>(() =>
            Service(client).PreviewAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal("key_unavailable", exception.Code);
    }

    [Theory]
    [InlineData("", "invalid_sitemap")]
    [InlineData("<urlset", "invalid_sitemap")]
    [InlineData("<urlset><url><loc /></url></urlset>", "invalid_sitemap")]
    [InlineData("<rss />", "invalid_sitemap")]
    [InlineData("<!DOCTYPE urlset [<!ENTITY test SYSTEM 'file:///nope'>]><urlset>&test;</urlset>", "invalid_sitemap")]
    [InlineData("<urlset xmlns='http://www.sitemaps.org/schemas/sitemap/0.9'/>", "empty_sitemap")]
    [InlineData("<sitemapindex xmlns='http://www.sitemaps.org/schemas/sitemap/0.9'/>", "sitemap_index_unsupported")]
    public async Task DiagnosesUnsupportedOrInvalidSitemaps(string xml, string code)
    {
        using var client = ReplyWith(Text(xml));

        var exception = await Assert.ThrowsAsync<IndexNowException>(() =>
            Service(client).PreviewAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal(code, exception.Code);
        if (code == "sitemap_index_unsupported")
        {
            Assert.Contains("urlset", exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("http://example.com/a")]
    [InlineData("https://example.net/a")]
    [InlineData("https://example.com:444/a")]
    [InlineData("https://example.com/a#fragment")]
    [InlineData("https://username:password@example.com/a")]
    public async Task RejectsNonCanonicalSitemapUrls(string url)
    {
        using var client = ReplyWith(Text(Sitemap(url)));

        var exception = await Assert.ThrowsAsync<IndexNowException>(() =>
            Service(client).PreviewAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal("sitemap_url_outside_origin", exception.Code);
        Assert.DoesNotContain(url, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SitemapEntryLimitCountsDuplicates()
    {
        using var client = ReplyWith(Text(Sitemap([
            .. Enumerable.Repeat("https://example.com/page", 50_001)
        ])));

        var exception = await Assert.ThrowsAsync<IndexNowException>(() =>
            Service(client).PreviewAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal("sitemap_too_large", exception.Code);
    }

    [Fact]
    public async Task ResponseLimitAppliesToBodiesWithoutAContentLength()
    {
        using var client = ReplyWith(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnseekableStream(new byte[5 * 1024 * 1024 + 1]))
        });

        var exception = await Assert.ThrowsAsync<IndexNowException>(() =>
            Service(client).PreviewAsync(Site, TestContext.Current.CancellationToken));

        Assert.Equal("sitemap_too_large", exception.Code);
    }

    [Fact]
    public async Task SitemapUrlsMustBeInTheKeyDirectory()
    {
        var site = Site with { KeyLocation = new Uri("https://example.com/catalog/indexnow-key.txt") };
        using var client = ReplyWith(Text(Sitemap("https://example.com/catalogue/a")), site);

        var exception = await Assert.ThrowsAsync<IndexNowException>(() =>
            Service(client).PreviewAsync(site, TestContext.Current.CancellationToken));

        Assert.Equal("sitemap_url_outside_origin", exception.Code);
    }

    [Fact]
    public async Task InvalidSiteLocationsAreRejectedBeforeAnyHttpRequest()
    {
        using var client = new HttpClient(new IndexNowTestSite((_, _) =>
            throw new InvalidOperationException("Unexpected HTTP request.")));

        await Assert.ThrowsAsync<ArgumentException>(() => Service(client).PreviewAsync(
            Site with { KeyLocation = new Uri("https://localhost/indexnow-key.txt") },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCancellationDuringBodyReadWinsOverAStreamFailure()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = ReplyWith(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancellingStream(cancellation))
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(client).PreviewAsync(Site, cancellation.Token));
    }

    private static IndexNowService Service(HttpClient client) =>
        new(client, Options.Create(new IndexNowOptions()), TimeProvider.System);

    private static HttpClient ReplyWith(HttpResponseMessage response, IndexNowSite? site = null)
    {
        site ??= Site;
        return new HttpClient(new IndexNowTestSite((request, _) =>
            Task.FromResult(request.RequestUri == site.KeyLocation ? Text(Key) : response)));
    }

    private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            throw new IOException("The connection closed during the read.");
        }
    }

    private sealed class UnseekableStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override bool CanSeek => false;
    }
}
