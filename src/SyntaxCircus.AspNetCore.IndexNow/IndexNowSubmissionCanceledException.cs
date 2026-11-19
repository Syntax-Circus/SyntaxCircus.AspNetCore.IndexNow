namespace SyntaxCircus.AspNetCore.IndexNow;

/// <summary>Reports the batch outcomes available when caller cancellation stops a submission.</summary>
public sealed class IndexNowSubmissionCanceledException : OperationCanceledException
{
    public IndexNowSubmissionCanceledException(
        IReadOnlyList<IndexNowBatchResult> batches, CancellationToken cancellationToken)
        : base("The IndexNow submission was canceled.", cancellationToken)
    {
        Batches = batches;
    }

    public IReadOnlyList<IndexNowBatchResult> Batches { get; }
}
