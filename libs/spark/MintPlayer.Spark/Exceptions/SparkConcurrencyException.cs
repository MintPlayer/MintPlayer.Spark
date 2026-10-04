namespace MintPlayer.Spark.Exceptions;

/// <summary>
/// Thrown when an update carries an Etag that no longer matches the server's current
/// change vector — i.e. the entity was modified by someone else between the caller's
/// read and write — or when the write itself found the document changed since it was
/// loaded (RavenDB's <c>ConcurrencyException</c>, kept as the inner exception).
/// Endpoint handlers translate this to HTTP 409 Conflict, never echoing <see cref="Exception.Message"/>:
/// it (and the inner exception's) carries change vectors. What the caller may see is
/// <see cref="Reason"/> and <see cref="UserMessage"/>.
/// </summary>
internal sealed class SparkConcurrencyException : Exception
{
    /// <summary>The row changed since the caller loaded it.</summary>
    public const string Changed = "changed";

    /// <summary>The row was deleted since the caller loaded it (#467, D15).</summary>
    public const string Deleted = "deleted";

    public string? ExpectedEtag { get; }
    public string? ActualEtag { get; }

    /// <summary><see cref="Changed"/> or <see cref="Deleted"/>; safe to send.</summary>
    public string Reason { get; } = Changed;

    /// <summary>
    /// A message safe to show — a bulk delete names the rows that changed (#467, D18). Null for a
    /// single row, where the client words it.
    /// </summary>
    public string? UserMessage { get; }

    public SparkConcurrencyException(string expectedEtag, string? actualEtag, string? userMessage = null)
        : base($"Optimistic concurrency check failed: expected etag '{expectedEtag}', actual '{actualEtag ?? "<none>"}'.")
    {
        ExpectedEtag = expectedEtag;
        ActualEtag = actualEtag;
        Reason = actualEtag is null ? Deleted : Changed;
        UserMessage = userMessage;
    }

    /// <summary>The write was refused by RavenDB: the document changed (or went) after this save loaded it.</summary>
    public SparkConcurrencyException(Exception innerException)
        : base("Optimistic concurrency check failed: the document changed while it was being saved.", innerException)
    {
    }

    /// <summary>
    /// The object the caller loaded no longer exists, or is no longer one they may see (#467, D15):
    /// "deleted by another user", never a resurrection.
    /// </summary>
    public static SparkConcurrencyException DeletedSinceLoaded(string expectedEtag) => new(expectedEtag, actualEtag: null);

    /// <summary>The object already exists (#467, D16).</summary>
    public const string Exists = "exists";

    private SparkConcurrencyException(string reason, string message) : base(message) => Reason = reason;

    /// <summary>
    /// A create whose natural id another row already holds (#467, D16). It used to become an edit of
    /// that row, but an edit must say which version it overwrites, and a create has none to say.
    /// </summary>
    public static SparkConcurrencyException AlreadyExists()
        => new(Exists, "A create collided with an existing row's natural id.");
}
