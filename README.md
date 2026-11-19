# SyntaxCircus.AspNetCore.IndexNow

A .NET 10 package for previewing a site's sitemap and explicitly submitting selected URLs to IndexNow.

## Set up a site

Choose a canonical public HTTPS origin. Deploy its sitemap at a public URL and put the IndexNow key in a public UTF-8 text file on the same origin, for example `/indexnow-key.txt`. The key must contain 8 to 128 ASCII letters, digits or dashes. A key file in a subdirectory only covers URLs below that directory. [IndexNow documentation](https://www.indexnow.org/documentation)

Register the service in the ASP.NET Core application's startup code.

```csharp
using SyntaxCircus.AspNetCore.IndexNow;

builder.Services.AddSyntaxCircusIndexNow();
```

Inject `IIndexNowService` where the application handles preview and explicit submission.

```csharp
var site = new IndexNowSite(
    new Uri("https://www.example.com/"),
    new Uri("https://www.example.com/sitemap.xml"),
    new Uri("https://www.example.com/indexnow-key.txt"));

var preview = await indexNow.PreviewAsync(site, cancellationToken);

// After the caller confirms which URLs to submit:
var result = await indexNow.SubmitAsync(site, selectedUrls, cancellationToken);
```

Preview checks the key and reads a sitemap containing a `urlset`. If the site uses a sitemap index, point the service at one of its URL sitemaps. The service limits downloaded file bodies to 5 MiB and a sitemap to 50,000 URL entries. Both operations reject off-origin URLs, credentials and fragments. The transport blocks redirects and connects only to verified public IP addresses.

Submission checks the key again, validates the complete selection and sends batches of at most 10,000 distinct URLs. A batch with status `200` was received. Status `202` means key validation is pending. Results describe submission receipt only. Failed batches have a safe `Error` and a nullable `StatusCode`. The service retries throttling, server errors and transient network failures up to twice, with any `Retry-After` delay capped at five seconds. [IndexNow response codes](https://www.indexnow.org/documentation#response-format)

Call `IndexNowValidation.NormalizeSubmissionUrls` to apply the submission's URL rules before starting the operation. It returns the original URL strings in first-seen order after removing ordinal duplicates. Invalid settings and invalid submission URLs raise `ArgumentException`. Ownership and sitemap failures raise `IndexNowException` with a stable `Code` and a display-safe `Message`.

Caller cancellation propagates through both operations. A canceled submission throws `IndexNowSubmissionCanceledException`, a subtype of `OperationCanceledException`. Its `Batches` preserve completed outcomes and include every remaining URL in a batch with a null status.

## Develop locally

```bash
dotnet build SyntaxCircus.AspNetCore.IndexNow.slnx -c Release
dotnet test SyntaxCircus.AspNetCore.IndexNow.slnx -c Release --no-build
dotnet pack SyntaxCircus.AspNetCore.IndexNow.slnx -c Release --no-build -o artifacts
```

Tests use deterministic HTTP and DNS doubles. CI builds, tests and packs the project, then uploads the build artifacts to the workflow run.
