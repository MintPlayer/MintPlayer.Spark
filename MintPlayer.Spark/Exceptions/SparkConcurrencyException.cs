namespace MintPlayer.Spark.Exceptions;

/// <summary>
/// Thrown when an update carries an Etag that no longer matches the server's current
/// change vector — i.e. the entity was modified by someone else between the caller's
/// read and write. Endpoint handlers translate this to HTTP 409 Conflict.
/// </summary>
internal sealed class SparkConcurrencyException : Exception
{
    public string ExpectedEtag { get; }
    public string? ActualEtag { get; }

    public SparkConcurrencyException(string expectedEtag, string? actualEtag)
        : base($"Optimistic concurrency check failed: expected etag '{expectedEtag}', actual '{actualEtag ?? "<none>"}'.")
    {
        ExpectedEtag = expectedEtag;
        ActualEtag = actualEtag;
    }
}
