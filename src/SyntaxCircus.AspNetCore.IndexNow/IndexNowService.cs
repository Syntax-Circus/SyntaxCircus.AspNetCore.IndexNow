using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;

namespace SyntaxCircus.AspNetCore.IndexNow;

internal sealed class IndexNowService(HttpClient httpClient, IOptions<IndexNowOptions> options, TimeProvider clock)
    : IIndexNowService
{
    private const int BatchSize = 10_000;
    private const int MaxRetries = 2;
    private static readonly Uri SubmissionEndpoint = new("https://api.indexnow.org/indexnow");
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<IndexNowPreview> PreviewAsync(IndexNowSite site, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SiteUrls.Validate(site);

        await VerifyKeyAsync(site, cancellationToken).ConfigureAwait(false);
        var content = await GetFileAsync(site.SitemapUrl, isKey: false, cancellationToken).ConfigureAwait(false);
        var urls = await SitemapReader.ReadAsync(site, content, cancellationToken).ConfigureAwait(false);
        return new IndexNowPreview(urls);
    }

    public async Task<IndexNowSubmissionResult> SubmitAsync(
        IndexNowSite site, IReadOnlyList<string> urls, CancellationToken cancellationToken = default)
    {
        var normalized = IndexNowValidation.NormalizeSubmissionUrls(site, urls);
        List<IndexNowBatchResult> batches = [];
        var completedUrlCount = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = await VerifyKeyAsync(site, cancellationToken).ConfigureAwait(false);
            foreach (var batch in normalized.Chunk(BatchSize))
            {
                batches.Add(await SubmitBatchAsync(site, key, Array.AsReadOnly(batch), cancellationToken).ConfigureAwait(false));
                completedUrlCount += batch.Length;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var batch in normalized.Skip(completedUrlCount).Chunk(BatchSize))
            {
                batches.Add(new IndexNowBatchResult(Array.AsReadOnly(batch), null,
                    "Submission canceled before the batch result was confirmed."));
            }

            throw new IndexNowSubmissionCanceledException(batches.AsReadOnly(), cancellationToken);
        }

        return new IndexNowSubmissionResult(batches.AsReadOnly());
    }

    private async Task<string> VerifyKeyAsync(IndexNowSite site, CancellationToken cancellationToken)
    {
        var bytes = await GetFileAsync(site.KeyLocation, isKey: true, cancellationToken).ConfigureAwait(false);
        string key;
        try
        {
            var text = StrictUtf8.GetString(bytes);
            key = (text.StartsWith('\uFEFF') ? text[1..] : text).Trim();
        }
        catch (DecoderFallbackException)
        {
            throw InvalidKey();
        }

        if (key.Length is < 8 or > 128 || key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw InvalidKey();
        }

        return key;
    }

    private static IndexNowException InvalidKey() =>
        new("invalid_key", "The public key file must contain 8 to 128 ASCII letters, digits or dashes.");

    private async Task<byte[]> GetFileAsync(Uri url, bool isKey, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.RequestTimeout);

        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return await ReadBoundedAsync(response.Content, isKey, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (IsNetworkFailure(exception))
        {
            // Return a safe setup error without surfacing server content, URLs or diagnostics.
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw isKey
            ? new IndexNowException("key_unavailable", "The public key file could not be read. Check its HTTPS address and public access.")
            : new IndexNowException("sitemap_unavailable", "The sitemap could not be read. Check its HTTPS address and public access.");
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, bool isKey, CancellationToken cancellationToken)
    {
        var limit = options.Value.MaxResponseBytes;
        if (content.Headers.ContentLength > limit)
        {
            throw FileTooLarge(isKey);
        }

        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var readSize = (int)Math.Min(buffer.Length, limit - body.Length + 1);
            var read = await stream.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return body.ToArray();
            }

            if (body.Length + read > limit)
            {
                throw FileTooLarge(isKey);
            }

            body.Write(buffer, 0, read);
        }
    }

    private static IndexNowException FileTooLarge(bool isKey) => isKey
        ? new("key_too_large", "The public key file exceeds the 5 MiB response limit.")
        : new("sitemap_too_large", "The sitemap exceeds the 5 MiB response limit.");

    private async Task<IndexNowBatchResult> SubmitBatchAsync(
        IndexNowSite site, string key, IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await PostAsync(site, key, urls, cancellationToken).ConfigureAwait(false);
            if (!outcome.Retry || attempt == MaxRetries)
            {
                return new IndexNowBatchResult(urls, outcome.StatusCode, outcome.Error);
            }

            var delay = GetRetryDelay(outcome.RetryAfter, attempt);
            await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PostOutcome> PostAsync(
        IndexNowSite site, string key, IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, SubmissionEndpoint)
        {
            Content = JsonContent.Create(new
            {
                host = site.CanonicalUrl.IdnHost,
                key,
                keyLocation = site.KeyLocation.AbsoluteUri,
                urlList = urls
            })
        };

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return status is 200 or 202
                ? new PostOutcome(status, null, false, null)
                : new PostOutcome(status, $"IndexNow returned HTTP {status}.", status is 429 or >= 500 and <= 599,
                    response.Headers.RetryAfter);
        }
        catch (Exception exception) when (IsNetworkFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retry = exception is not HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError };
            return new PostOutcome(null, "IndexNow did not return a response.", retry, null);
        }
    }

    private TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date - clock.GetUtcNow())
            ?? options.Value.InitialRetryDelay * (1 << attempt);
        return TimeSpan.FromTicks(Math.Clamp(delay.Ticks, 0, options.Value.MaxRetryDelay.Ticks));
    }

    private static bool IsNetworkFailure(Exception exception) =>
        exception is HttpRequestException or IOException or OperationCanceledException;

    private readonly record struct PostOutcome(int? StatusCode, string? Error, bool Retry, RetryConditionHeaderValue? RetryAfter);
}
