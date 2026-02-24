namespace MintPlayer.Spark.Abstractions.Actions;

/// <summary>
/// Context passed to a custom action when executed.
/// </summary>
public class CustomActionArgs
{
    /// <summary>
    /// The parent PersistentObject (when invoked from a detail view).
    /// Null when invoked from a query with no parent.
    /// </summary>
    public PersistentObject? Parent { get; set; }

    /// <summary>
    /// Selected items from a query (when invoked from a list view).
    /// Empty when invoked from a detail view.
    /// </summary>
    public PersistentObject[] SelectedItems { get; set; } = [];
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
