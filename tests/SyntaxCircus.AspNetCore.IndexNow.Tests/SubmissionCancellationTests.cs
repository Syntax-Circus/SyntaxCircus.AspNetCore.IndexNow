using System.Net;
using Microsoft.Extensions.Options;
using static SyntaxCircus.AspNetCore.IndexNow.Tests.IndexNowTestSite;

namespace SyntaxCircus.AspNetCore.IndexNow.Tests;

public sealed class SubmissionCancellationTests
{
    [Fact]
    public async Task CancellationDuringSecondBatchRetainsReceiptForFirstBatch()
    {
        var firstBatch = Enumerable.Range(0, 10_000).Select(i => $"https://example.com/p/{i}").ToArray();
        const string lastPage = "https://example.com/final";
        using var cancellation = new CancellationTokenSource();
        var posts = 0;
        using var client = new HttpClient(new IndexNowTestSite((request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Text(Key));
            }

            if (++posts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The second POST should be canceled.");
        }));

        var exception = await Assert.ThrowsAsync<IndexNowSubmissionCanceledException>(() =>
            Service(client).SubmitAsync(Site, [.. firstBatch, lastPage], cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(2, posts);
        Assert.Collection(exception.Batches, completed =>
        {
            Assert.Equal(200, completed.StatusCode);
            Assert.Null(completed.Error);
            Assert.Equal(firstBatch, completed.Urls);
        }, unconfirmed =>
        {
            Assert.Null(unconfirmed.StatusCode);
            Assert.NotNull(unconfirmed.Error);
            Assert.Equal([lastPage], unconfirmed.Urls);
        });
    }

    [Fact]
    public async Task CancellationBeforeKeyReadReportsEveryNormalizedUrlAsUnconfirmed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new IndexNowTestSite((_, _) =>
            throw new InvalidOperationException("A canceled submission must make no requests.")));
        const string page = "https://example.com/page";

        var exception = await Assert.ThrowsAsync<IndexNowSubmissionCanceledException>(() =>
            Service(client).SubmitAsync(Site, [page, page], cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var batch = Assert.Single(exception.Batches);
        Assert.Null(batch.StatusCode);
        Assert.NotNull(batch.Error);
        Assert.Equal([page], batch.Urls);
    }

    private static IndexNowService Service(HttpClient client) =>
        new(client, Options.Create(new IndexNowOptions()), TimeProvider.System);
}
