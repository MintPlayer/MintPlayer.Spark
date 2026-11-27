namespace MintPlayer.Spark.Testing;

/// <summary>
/// An index a test depends on is broken or absent: it faulted while building, or it was never
/// registered on the server at all.
/// <para>
/// Distinct from <see cref="TimeoutException"/> on purpose. A timeout says "indexing did not keep
/// up" — retry, raise the limit, or look at load. This says "the index is not going to work",
/// which no amount of waiting fixes. Collapsing the two forces the message to carry a distinction
/// the type should be making, and leaves anyone reading a red build guessing which they hit.
/// </para>
/// </summary>
public sealed class RavenIndexDeploymentException : Exception
{
    public RavenIndexDeploymentException(string message, IReadOnlyCollection<string> faultedIndexes, IReadOnlyCollection<string> missingIndexes)
        : base(message)
    {
        FaultedIndexes = faultedIndexes;
        MissingIndexes = missingIndexes;
    }

    /// <summary>Indexes that reached <c>IndexState.Error</c> — typically a map/reduce that failed to compile or threw on a document.</summary>
    public IReadOnlyCollection<string> FaultedIndexes { get; }

    /// <summary>Indexes that were expected to exist but never appeared in the database statistics.</summary>
    public IReadOnlyCollection<string> MissingIndexes { get; }
}
