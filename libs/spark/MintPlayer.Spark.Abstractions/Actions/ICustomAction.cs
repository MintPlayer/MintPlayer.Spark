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

    /// <summary>The selected items exactly as the client submitted them — untrusted values.</summary>
    public PersistentObject[] SubmittedSelectedItems { get; set; } = [];
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
