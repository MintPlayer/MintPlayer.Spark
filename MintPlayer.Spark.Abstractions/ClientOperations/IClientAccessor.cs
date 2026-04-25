namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Backend accumulator for client operations. Scoped per HTTP request. Exposed on
/// <see cref="IManager.Client"/>. Non-blocking methods append to the accumulator and
/// return; the operations are drained into the response envelope by the endpoint.
/// Blocking concerns (retry) go through <see cref="IManager.Retry"/>, which also
/// accumulates onto this surface internally.
/// </summary>
public interface IClientAccessor
{
    /// <summary>
    /// The currently-accumulated operations in emission order. Drained by the
    /// endpoint on response egress.
    /// </summary>
    IReadOnlyList<ClientOperation> Operations { get; }

    // --- Navigate -------------------------------------------------------

    /// <summary>Navigate to the given PersistentObject's detail view.</summary>
    void Navigate(PersistentObject po);

    /// <summary>Navigate by ObjectTypeId + id.</summary>
    void Navigate(Guid objectTypeId, string id);

    /// <summary>Navigate to a named route.</summary>
    void Navigate(string routeName);

    // --- Notify ---------------------------------------------------------

    /// <summary>Show a toast on the frontend.</summary>
    void Notify(string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null);

    // --- Refresh --------------------------------------------------------

    /// <summary>Patch the named attribute on the given PO if it's currently displayed.</summary>
    void RefreshAttribute(PersistentObject po, string attributeName);

    /// <summary>
    /// Patch a specific attribute with a new server-computed <paramref name="value"/>
    /// when the caller doesn't have a PO reference at hand.
    /// </summary>
    void RefreshAttribute(Guid objectTypeId, string id, string attributeName, object? value);

    /// <summary>Re-execute a named query if it's currently displayed.</summary>
    void RefreshQuery(string queryId);

    // --- DisableAction overloads ---------------------------------------
    //
    // Spark keeps PersistentObject / Query as pure DTOs (no service back-reference),
    // so the ergonomic "po.DisableActions(...)" shape from Vidyano doesn't port.
    // Overloads on this accessor recover concision without coupling the DTO to
    // framework services.

    /// <summary>Disable actions while the given PO is displayed.</summary>
    void DisableActionsOn(PersistentObject po, params string[] actionNames);

    /// <summary>Disable actions for the PO identified by (<paramref name="objectTypeId"/>, <paramref name="id"/>).</summary>
    void DisableActionsOn(Guid objectTypeId, string id, params string[] actionNames);

    /// <summary>Disable actions while the named query is displayed.</summary>
    void DisableQueryActions(string queryId, params string[] actionNames);

    /// <summary>
    /// Disable actions on whatever the current endpoint is returning (current-response
    /// target — PO for PO endpoints, query for query-execute, etc.).
    /// </summary>
    void DisableActions(params string[] actionNames);

    /// <summary>
    /// Disable actions for the duration of the user's session. Rare — prefer
    /// <c>security.json</c> for permission-driven disables.
    /// </summary>
    void DisableActionsForSession(params string[] actionNames);
}
