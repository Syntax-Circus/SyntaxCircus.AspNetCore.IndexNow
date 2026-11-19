using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using static SyntaxCircus.AspNetCore.IndexNow.Tests.IndexNowTestSite;

namespace SyntaxCircus.AspNetCore.IndexNow.Tests;

public sealed class SubmissionTests
{
    private const string Page = "https://example.com/page";

    [Fact]
    public async Task SubmissionRechecksOwnershipOnEachCall()
    {
        var keyReads = 0;
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Text(++keyReads == 1 ? Key : "different_invalid_key"));
            }

            Assert.Equal(1, keyReads);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var service = Service(client);
        var token = TestContext.Current.CancellationToken;

        await service.SubmitAsync(Site, [Page], token);
        var exception = await Assert.ThrowsAsync<IndexNowException>(() => service.SubmitAsync(Site, [Page], token));

        Assert.Equal("invalid_key", exception.Code);
        Assert.Equal(2, keyReads);
    }

    [Fact]
    public async Task AnInvalidLastUrlPreventsAllBatchesFromBeingSent()
    {
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            Assert.NotEqual(HttpMethod.Post, request.Method);
            return Task.FromResult(Text(Key));
        }));
        string[] urls = [.. Enumerable.Range(0, 10_000).Select(i => $"https://example.com/p/{i}"), "https://other.example/p"];

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(client).SubmitAsync(Site, urls, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeduplicatesOrdinallyAndSendsAtMostTenThousandUrlsPerBatch()
    {
        var pages = Enumerable.Range(0, 9_999).Select(i => $"https://example.com/p/{i}").ToList();
        pages.Add("https://example.com/A");
        pages.Add("https://example.com/a");
        string[] input = [pages[0], .. pages, pages[^1]];
        List<string[]> posted = [];
        using var client = new HttpClient(new IndexNowTestSite(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(Site.KeyLocation, request.RequestUri);
                return Text(Key);
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.indexnow.org/indexnow", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var json = payload.RootElement;
            Assert.Equal("example.com", json.GetProperty("host").GetString());
            Assert.Equal(Key, json.GetProperty("key").GetString());
            Assert.Equal("https://example.com/indexnow-key.txt", json.GetProperty("keyLocation").GetString());
            posted.Add(json.GetProperty("urlList").EnumerateArray().Select(url => url.GetString()!).ToArray());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var result = await Service(client).SubmitAsync(Site, input, TestContext.Current.CancellationToken);

        Assert.Equal(2, posted.Count);
        Assert.Equal(10_000, posted[0].Length);
        Assert.Equal(["https://example.com/a"], posted[1]);
        Assert.Equal(pages, posted.SelectMany(batch => batch));
        Assert.Equal(pages, result.Batches.SelectMany(batch => batch.Urls));
    }

    [Theory]
    [InlineData(200, 1, true)]
    [InlineData(202, 1, true)]
    [InlineData(204, 1, false)]
    [InlineData(307, 1, false)]
    [InlineData(400, 1, false)]
    [InlineData(403, 1, false)]
    [InlineData(422, 1, false)]
    [InlineData(429, 3, false)]
    [InlineData(500, 3, false)]
    [InlineData(503, 3, false)]
    public async Task ClassifiesStatusAndRetriesOnlyThrottlingOrServerErrors(int status, int attempts, bool accepted)
    {
        var posts = 0;
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Text(Key));
            }

            posts++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("untrusted provider body")
            });
        }));

        var result = await Service(client).SubmitAsync(Site, [Page], TestContext.Current.CancellationToken);

        Assert.Equal(attempts, posts);
        var batch = Assert.Single(result.Batches);
        Assert.Equal(status, batch.StatusCode);
        if (accepted)
        {
            Assert.Null(batch.Error);
        }
        else
        {
            Assert.NotNull(batch.Error);
            Assert.DoesNotContain("untrusted provider body", batch.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, 3, null)]
    [InlineData(true, 2, 200)]
    public async Task RetriesNetworkFailuresAndReturnsTheFinalBatchResult(bool recovers, int attempts, int? status)
    {
        var posts = 0;
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Text(Key));
            }

            if (++posts == 2 && recovers)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            throw new HttpRequestException("Private diagnostics");
        }));

        var result = await Service(client).SubmitAsync(Site, [Page], TestContext.Current.CancellationToken);

        Assert.Equal(attempts, posts);
        var batch = Assert.Single(result.Batches);
        Assert.Equal(status, batch.StatusCode);
        Assert.Equal(!recovers, batch.Error is not null);
        Assert.DoesNotContain("Private diagnostics", batch.Error ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 5)]
    public async Task RespectsRetryAfterDeltaOrDateWithinTheDelayLimit(bool useDate, int seconds)
    {
        var clock = new RecordingTimeProvider();
        var posts = 0;
        using var client = new HttpClient(new IndexNowTestSite((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Text(Key));
            }

            if (++posts == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = useDate
                    ? new RetryConditionHeaderValue(clock.GetUtcNow().AddHours(1))
                    : new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }));
        var service = new IndexNowService(client, Options.Create(new IndexNowOptions()), clock);

        var result = await service.SubmitAsync(Site, [Page], TestContext.Current.CancellationToken);

        Assert.Equal(202, Assert.Single(result.Batches).StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(seconds), Assert.Single(clock.Delays));
    }

    [Fact]
    public async Task CallerCancellationDuringPostPropagatesWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var posts = 0;
        using var client = new HttpClient(new IndexNowTestSite((request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Text(Key));
            }

            posts++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(client).SubmitAsync(Site, [Page], cancellation.Token));

        Assert.Equal(1, posts);
    }

    private static IndexNowService Service(HttpClient client) => new(client, Options.Create(new IndexNowOptions
    {
        InitialRetryDelay = TimeSpan.Zero,
        MaxRetryDelay = TimeSpan.Zero
    }), TimeProvider.System);

    private sealed class RecordingTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = [];

        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }
}
