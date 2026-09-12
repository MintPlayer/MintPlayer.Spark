using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed class PersistentObjectRequest : IRetryableRequest
{
    public Abstractions.PersistentObject? PersistentObject { get; set; }
    public RetryResult[]? RetryResults { get; set; }
}
