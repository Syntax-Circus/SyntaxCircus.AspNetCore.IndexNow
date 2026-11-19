using System.Xml;
using System.Xml.Linq;

namespace SyntaxCircus.AspNetCore.IndexNow;

internal static class SitemapReader
{
    private const int MaxEntries = 50_000;
    private static readonly XNamespace SitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

    internal static async Task<IReadOnlyList<string>> ReadAsync(IndexNowSite site, byte[] content, CancellationToken cancellationToken)
    {
        XDocument document;
        try
        {
            using var body = new MemoryStream(content, writable: false);
            using var reader = XmlReader.Create(body, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (XmlException)
        {
            throw InvalidSitemap();
        }

        var root = document.Root;
        if (root is null || (root.Name.Namespace != SitemapNamespace && root.Name.Namespace != XNamespace.None))
        {
            throw InvalidSitemap();
        }

        if (root.Name.LocalName == "sitemapindex")
        {
            throw new IndexNowException("sitemap_index_unsupported", "Use a sitemap containing a urlset. Sitemap indexes are not supported.");
        }

        if (root.Name.LocalName != "urlset")
        {
            throw InvalidSitemap();
        }

        var ns = root.Name.Namespace;
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> urls = [];
        var count = 0;
        foreach (var entry in root.Elements(ns + "url"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaxEntries)
            {
                throw new IndexNowException("sitemap_too_large", "Use a sitemap with at most 50,000 URL entries.");
            }

            var locations = entry.Elements(ns + "loc").Take(2).ToArray();
            if (locations.Length != 1 || locations[0].HasElements || string.IsNullOrWhiteSpace(locations[0].Value))
            {
                throw InvalidSitemap();
            }

            var url = locations[0].Value.Trim();
            if (!SiteUrls.IsSubmittable(site, url))
            {
                throw new IndexNowException("sitemap_url_outside_origin",
                    "Every sitemap URL must use the canonical HTTPS origin and be within the public key directory.");
            }

            if (seen.Add(url))
            {
                urls.Add(url);
            }
        }

        if (urls.Count == 0)
        {
            throw new IndexNowException("empty_sitemap", "The sitemap contains no URL entries.");
        }

        return urls.AsReadOnly();
    }

    private static IndexNowException InvalidSitemap() =>
        new("invalid_sitemap", "The sitemap must be valid XML with a urlset and one loc per URL.");
}
