using static SyntaxCircus.AspNetCore.IndexNow.Tests.IndexNowTestSite;

namespace SyntaxCircus.AspNetCore.IndexNow.Tests;

public sealed class SubmissionValidationTests
{
    private static readonly IndexNowSite Catalog = Site with
    {
        KeyLocation = new Uri("https://example.com/catalog/indexnow-key.txt")
    };

    [Fact]
    public void DeduplicatesOrdinallyAndPreservesWellFormedOriginalStrings()
    {
        const string first = "https://EXAMPLE.COM/catalog/escaped%20path";
        const string second = "https://example.com/catalog/escaped%20path";

        Assert.Equal([first, second], IndexNowValidation.NormalizeSubmissionUrls(Catalog, [first, second, first]));
    }

    [Theory]
    [InlineData("https://example.com/catalogue/item")]
    [InlineData("https://example.com/catalog/../outside")]
    [InlineData("https://example.com/catalog/has space")]
    [InlineData("https://example.com/catalog/has\\backslash")]
    public void RejectsUrlsOutsideKeyScopeAndMalformedOriginalStrings(string url)
    {
        Assert.Throws<ArgumentException>("urls", () =>
            IndexNowValidation.NormalizeSubmissionUrls(Catalog, ["https://example.com/catalog/item", url]));
    }
}
