using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Streaming;

/// <summary>
/// One batch of a streaming query: the columns describing it, and the rows themselves.
/// </summary>
/// <remarks>
/// The columns are carried on every batch but sent only with the snapshot — a stream is one result
/// whose rows arrive over time, so its shape is fixed when it opens. Repeating them here rather than
/// hoisting them out of the sequence keeps the executor a plain async iterator; deciding what
/// reaches the wire is the transport's job.
/// </remarks>
internal sealed record StreamingQueryBatch(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<QueryResultItem> Items);

public interface IStreamingQueryExecutor
{
    internal IAsyncEnumerable<StreamingQueryBatch> ExecuteStreamingQueryAsync(SparkQuery query, CancellationToken cancellationToken);
}
