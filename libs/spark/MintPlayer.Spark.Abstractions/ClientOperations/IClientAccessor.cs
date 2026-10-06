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

    /// <summary>
    /// Show a toast in the user's language: every translation travels and the client picks the one of
    /// the language its user chose; the plain message is resolved from the request's culture.
    /// </summary>
    void Notify(TranslatedString message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null);

    // --- Refresh --------------------------------------------------------

    /// <summary>Patch the named attribute on the given PO if it's currently displayed.</summary>
    void RefreshAttribute(PersistentObject po, string attributeName);

    /// <summary>
    /// Patch a specific attribute with a new server-computed <paramref name="value"/>
    /// when the caller doesn't have a PO reference at hand. Like the object overload, a no-op for an
    /// attribute the caller may not read, and a throw for a type or attribute the model does not have.
    /// </summary>
    void RefreshAttribute(Guid objectTypeId, string id, string attributeName, object? value);

    /// <summary>
    /// Re-execute a query wherever it's currently displayed. <paramref name="queryId"/> is the
    /// query's id or its alias, matched case-insensitively — not its name. Every grid showing that
    /// query re-fetches once, whichever of the two forms it was opened with.
    /// </summary>
    void RefreshQuery(string queryId);

    // The DisableAction overloads (DisableActionsOn / DisableQueryActions / DisableActions /
    // DisableActionsForSession) are deleted (#460, D13). They emitted a client operation no client
    // honoured, the detail path dropped them, and nothing enforced them at submit. Disabling an action
    // is now one hook — OnDisableActionsAsync on the actions class — which the framework asks at load
    // and again at submit.
}
