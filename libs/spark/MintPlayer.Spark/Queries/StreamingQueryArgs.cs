using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Queries;

/// <summary>
/// Context passed to a streaming query method on an Actions class.
/// </summary>
public sealed class StreamingQueryArgs
{
    /// <summary>
    /// The SparkQuery being executed.
    /// </summary>
    public required SparkQuery Query { get; set; }

    /// <summary>
    /// The RavenDB async document session for database access.
    /// </summary>
    public required IAsyncDocumentSession Session { get; set; }

    /// <summary>
    /// Cancellation token that triggers when the WebSocket connection closes.
    /// </summary>
    public required CancellationToken CancellationToken { get; set; }
}
