using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Endpoints.Actions;

internal sealed class CustomActionRequest
{
    public Abstractions.PersistentObject? Parent { get; set; }
    public Abstractions.PersistentObject[]? SelectedItems { get; set; }
    public RetryResult[]? RetryResults { get; set; }
}
