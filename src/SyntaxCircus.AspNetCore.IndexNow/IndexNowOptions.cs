namespace SyntaxCircus.AspNetCore.IndexNow;

internal sealed class IndexNowOptions
{
    public int MaxResponseBytes { get; } = 5 * 1024 * 1024;

    public TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(30);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}
