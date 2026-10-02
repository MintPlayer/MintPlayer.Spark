namespace MintPlayer.Spark.Exceptions;

/// <summary>
/// Thrown when an update carries an Etag that no longer matches the server's current
/// change vector — i.e. the entity was modified by someone else between the caller's
/// read and write — or when the write itself found the document changed since it was
/// loaded (RavenDB's <c>ConcurrencyException</c>, kept as the inner exception).
/// Endpoint handlers translate this to HTTP 409 Conflict, never echoing the message:
/// it (and the inner exception's) carries change vectors.
/// </summary>
internal sealed class SparkConcurrencyException : Exception
{
    public string? ExpectedEtag { get; }
    public string? ActualEtag { get; }

    public SparkConcurrencyException(string expectedEtag, string? actualEtag)
        : base($"Optimistic concurrency check failed: expected etag '{expectedEtag}', actual '{actualEtag ?? "<none>"}'.")
    {
        ExpectedEtag = expectedEtag;
        ActualEtag = actualEtag;
    }

    /// <summary>The write was refused by RavenDB: the document changed after this save loaded it.</summary>
    public SparkConcurrencyException(Exception innerException)
        : base("Optimistic concurrency check failed: the document changed while it was being saved.", innerException)
    {
    }
}
