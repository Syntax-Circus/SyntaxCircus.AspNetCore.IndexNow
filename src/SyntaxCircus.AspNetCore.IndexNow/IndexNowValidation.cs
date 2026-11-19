namespace SyntaxCircus.AspNetCore.IndexNow;

/// <summary>Validates and deduplicates a submission without making any HTTP requests.</summary>
public static class IndexNowValidation
{
    /// <summary>Returns the valid original URL strings in their first-seen order.</summary>
    public static string[] NormalizeSubmissionUrls(IndexNowSite site, IReadOnlyList<string> urls)
    {
        SiteUrls.Validate(site);
        ArgumentNullException.ThrowIfNull(urls);
        if (urls.Count == 0)
        {
            throw new ArgumentException("Provide at least one URL to submit.", nameof(urls));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> unique = [];
        foreach (var url in urls)
        {
            if (!SiteUrls.IsSubmittable(site, url))
            {
                throw new ArgumentException("Every URL must use the canonical HTTPS origin and be within the public key directory.", nameof(urls));
            }

            if (seen.Add(url))
            {
                unique.Add(url);
            }
        }

        return unique.ToArray();
    }
}
