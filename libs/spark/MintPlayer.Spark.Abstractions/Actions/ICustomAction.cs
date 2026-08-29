namespace MintPlayer.Spark.Abstractions.Actions;

/// <summary>
/// Context passed to a custom action when executed.
/// <para>
/// <see cref="Parent"/> and <see cref="SelectedItems"/> are <b>server-loaded and row-checked</b>:
/// the framework re-resolves the ids the client named through the same row-gated read path as
/// every other load, so the action can trust them as current, visible state. The raw client
/// payload — which is just what the caller typed — remains available as
/// <see cref="SubmittedParent"/>/<see cref="SubmittedSelectedItems"/> for actions that need the
/// submitted (possibly edited, possibly unsaved) values.
/// </para>
/// <para>
/// There is no submitted counterpart for the SELECTION, and that is the row/entity separation
/// showing through: a selected row is named by an id, not submitted as an object. A grid row is a
/// projection the client was handed, never a document it may hand back, so the only thing worth
/// carrying across the wire is which rows were picked.
/// </para>
/// </summary>
public class CustomActionArgs
{
    /// <summary>
    /// The parent object (when invoked from a detail view), re-loaded server-side and row-checked.
    /// Null when the request named no parent (or named one without an id — an unsaved form's
    /// submitted state is in <see cref="SubmittedParent"/>).
    /// </summary>
    public PersistentObject? Parent { get; set; }

    /// <summary>
    /// Selected items from a query (when invoked from a list view), each re-loaded server-side
    /// and row-checked. Empty when invoked from a detail view.
    /// </summary>
    public PersistentObject[] SelectedItems { get; set; } = [];

    /// <summary>The parent exactly as the client submitted it — untrusted values, for actions that edit.</summary>
    public PersistentObject? SubmittedParent { get; set; }

    /// <summary>
    /// The object whose detail page this query was rendered on, when the action was invoked from a
    /// <b>sub-query</b>. Null on a top-level query page and on a detail-page invocation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ Not the same thing as <see cref="Parent"/>, and the distinction is the whole reason this
    /// exists. <see cref="Parent"/> is an object <em>of this action's own type</em> — the car whose
    /// detail page you clicked from — and is resolved under the route's type on purpose. This is the
    /// <em>container</em>, which is a different type entirely: the cars listed on a company's page
    /// are Cars, the page is a Company.
    /// </para>
    /// <para>
    /// Resolved through the same row-gated read path as any other load, under <b>its own</b> type,
    /// so the caller must be allowed to read it — a container they may not see refuses the request
    /// rather than arriving as a fact. It mirrors <c>CustomQueryArgs.Parent</c>, which is how the
    /// query that produced these rows was filtered in the first place.
    /// </para>
    /// </remarks>
    public PersistentObject? QueryParent { get; set; }

    /// <summary>The entity type name of <see cref="QueryParent"/>, e.g. <c>"Company"</c>.</summary>
    public string? QueryParentType { get; set; }

    /// <summary>The ids the client named, in submitted order — untrusted input, before resolution.</summary>
    /// <remarks>
    /// Rarely needed: <see cref="SelectedItems"/> is these ids resolved and row-checked, and the
    /// endpoint refuses the whole request if any of them did not resolve, so this cannot be a
    /// shorter list dressed up as a complete one.
    /// </remarks>
    public string[] SubmittedSelectedItemIds { get; set; } = [];
}

/// <summary>
/// Interface for custom actions. Implement this to create a custom action.
/// </summary>
public interface ICustomAction
{
    /// <summary>
    /// Executes the custom action.
    /// Navigate/Notify capabilities will be added in a future phase via IManager
    /// (same mechanism used by PersistentObject Actions classes).
    /// </summary>
    Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default);
}
