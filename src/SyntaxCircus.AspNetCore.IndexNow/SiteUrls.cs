using System.Net;
using System.Net.Sockets;

namespace SyntaxCircus.AspNetCore.IndexNow;

internal static class SiteUrls
{
    internal static void Validate(IndexNowSite site)
    {
        ArgumentNullException.ThrowIfNull(site);

        var canonical = site.CanonicalUrl;
        if (!IsPublicHttpsUrl(canonical) || canonical.AbsolutePath != "/" || canonical.Query.Length != 0)
        {
            throw new ArgumentException("The canonical URL must be a public HTTPS origin.", nameof(site));
        }

        if (!MatchesOrigin(canonical, site.KeyLocation) || !MatchesOrigin(canonical, site.SitemapUrl))
        {
            throw new ArgumentException("The sitemap and public key must use the canonical HTTPS origin.", nameof(site));
        }
    }

    internal static bool IsSubmittable(IndexNowSite site, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !MatchesOrigin(site.CanonicalUrl, uri))
        {
            return false;
        }

        var keyPath = site.KeyLocation.AbsolutePath;
        var directory = keyPath.AsSpan(0, keyPath.LastIndexOf('/') + 1);
        return uri.AbsolutePath.AsSpan().StartsWith(directory, StringComparison.Ordinal);
    }

    private static bool MatchesOrigin(Uri canonical, Uri uri) =>
        IsPublicHttpsUrl(uri) && uri.Port == canonical.Port
        && string.Equals(uri.IdnHost, canonical.IdnHost, StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicHttpsUrl(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsWellFormedOriginalString()
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback)
        {
            return false;
        }

        if (IPAddress.TryParse(uri.IdnHost, out var address))
        {
            return GuardedTransport.IsPublicAddress(address)
                && (address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId == 0);
        }

        return uri.HostNameType == UriHostNameType.Dns;
    }
}
