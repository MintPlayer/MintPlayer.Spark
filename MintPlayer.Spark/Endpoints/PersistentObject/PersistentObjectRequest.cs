using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed class PersistentObjectRequest
{
    public Abstractions.PersistentObject? PersistentObject { get; set; }
    public RetryResult[]? RetryResults { get; set; }
}
