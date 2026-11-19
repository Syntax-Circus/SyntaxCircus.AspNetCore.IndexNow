using System.Net;
using System.Text;

namespace SyntaxCircus.AspNetCore.IndexNow.Tests;

internal sealed class IndexNowTestSite(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    internal const string Key = "public-test-key";

    internal static IndexNowSite Site { get; } = new(
        new Uri("https://example.com/"),
        new Uri("https://example.com/sitemap.xml"),
        new Uri("https://example.com/indexnow-key.txt"));

    internal static HttpResponseMessage Text(string text, string mediaType = "text/plain") =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    internal static string Sitemap(params string[] urls) =>
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        string.Concat(urls.Select(url => $"<url><loc>{url}</loc></url>")) +
        "</urlset>";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => send(request, cancellationToken);
}
