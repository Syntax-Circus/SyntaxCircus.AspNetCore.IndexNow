namespace SyntaxCircus.AspNetCore.IndexNow;

/// <summary>The canonical origin, sitemap and public key locations for a site.</summary>
public sealed record IndexNowSite(Uri CanonicalUrl, Uri SitemapUrl, Uri KeyLocation);

/// <summary>The distinct canonical URLs discovered in a sitemap.</summary>
public sealed record IndexNowPreview(IReadOnlyList<string> Urls);

/// <summary>The response to one batch. A null error denotes receipt or pending key validation.</summary>
public sealed record IndexNowBatchResult(IReadOnlyList<string> Urls, int? StatusCode, string? Error);

/// <summary>The response to each batch in submission order.</summary>
public sealed record IndexNowSubmissionResult(IReadOnlyList<IndexNowBatchResult> Batches);

/// <summary>Previews sitemap URLs and explicitly submits URLs to IndexNow.</summary>
public interface IIndexNowService
{
    Task<IndexNowPreview> PreviewAsync(IndexNowSite site, CancellationToken cancellationToken = default);

    Task<IndexNowSubmissionResult> SubmitAsync(
        IndexNowSite site, IReadOnlyList<string> urls, CancellationToken cancellationToken = default);
}
