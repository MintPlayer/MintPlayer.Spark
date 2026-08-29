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

    /// <summary>
    /// The object whose detail page the query was rendered on, when the action was invoked from a
    /// <b>sub-query</b>. Named by id and type, exactly as <c>GET /spark/queries/…/execute</c> names
    /// it, and resolved under <see cref="ParentType"/> rather than under the action's own type.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately NOT <see cref="Parent"/>. That one is the route type's own object — the
    /// detail-page invocation — and is loaded under the route's entity type on purpose (security
    /// sweep C3). A sub-query's container is a <em>different type</em>: the cars listed on a
    /// company's page are Cars, the container is a Company. Reusing <c>Parent</c> for it would ask
    /// the server to load a Company id as a Car, which the collection guard refuses — correctly.
    /// </remarks>
    public string? ParentId { get; set; }

    /// <inheritdoc cref="ParentId"/>
    public string? ParentType { get; set; }

    public RetryResult[]? RetryResults { get; set; }
}
