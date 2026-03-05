using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Streaming;

public interface IStreamingQueryExecutor
{
    IAsyncEnumerable<PersistentObject[]> ExecuteStreamingQueryAsync(SparkQuery query, CancellationToken cancellationToken);
}
