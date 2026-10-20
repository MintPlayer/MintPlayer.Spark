using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Exceptions;

/// <summary>
/// Thrown when an update carries an Etag that no longer matches the server's current
/// change vector — i.e. the entity was modified by someone else between the caller's
/// read and write — or when the write itself found the document changed since it was
/// loaded (RavenDB's <c>ConcurrencyException</c>, kept as the inner exception).
/// Endpoint handlers translate this to HTTP 409 Conflict, never echoing <see cref="Exception.Message"/>:
/// it (and the inner exception's) carries change vectors. What the caller may see is
/// <see cref="SparkConflictException.Reason"/> and <see cref="SparkConflictException.UserMessage"/>.
/// The public <see cref="SparkConflictException"/> is what endpoints catch, so an add-on's conflict answers the same.
/// </summary>
internal sealed class SparkConcurrencyException : SparkConflictException
{
    public string? ExpectedEtag { get; }

    public string? ActualEtag { get; }

    public SparkConcurrencyException(string expectedEtag, string? actualEtag, string? userMessage = null)
        : base(actualEtag is null ? Deleted : Changed,
            $"Optimistic concurrency check failed: expected etag '{expectedEtag}', actual '{actualEtag ?? "<none>"}'.",
            userMessage, null, null)
    {
        ExpectedEtag = expectedEtag;
        ActualEtag = actualEtag;
    }

    /// <summary>The write was refused by RavenDB: the document changed (or went) after this save loaded it.</summary>
    public SparkConcurrencyException(Exception innerException)
        : base(Changed, "Optimistic concurrency check failed: the document changed while it was being saved.", null, null, innerException)
    {
    }

    /// <summary>
    /// The object the caller loaded no longer exists, or is no longer one they may see (#467, D15):
    /// "deleted by another user", never a resurrection.
    /// </summary>
    public static SparkConcurrencyException DeletedSinceLoaded(string expectedEtag) => new(expectedEtag, actualEtag: null);

    private SparkConcurrencyException(string reason, string message) : base(reason, message, null, null, null)
    {
    }

    /// <summary>
    /// A create whose natural id another row already holds (#467, D16). It used to become an edit of
    /// that row, but an edit must say which version it overwrites, and a create has none to say.
    /// </summary>
    public static SparkConcurrencyException AlreadyExists()
        => new(Exists, "A create collided with an existing row's natural id.");
}
