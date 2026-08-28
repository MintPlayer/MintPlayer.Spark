using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Endpoints.Actions;

internal sealed class CustomActionRequest
{
    public Abstractions.PersistentObject? Parent { get; set; }
    /// <summary>
    /// The ids of the selected rows. Ids, not objects: a grid row is a projection, and the server
    /// re-materializes each one through the row-gated read path before an action sees it.
    /// </summary>
    public string[]? SelectedItemIds { get; set; }
    public RetryResult[]? RetryResults { get; set; }
}
