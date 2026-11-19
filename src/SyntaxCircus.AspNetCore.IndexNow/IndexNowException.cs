namespace SyntaxCircus.AspNetCore.IndexNow;

/// <summary>An ownership or sitemap error with a code and a message suitable for display.</summary>
public sealed class IndexNowException : Exception
{
    public IndexNowException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
