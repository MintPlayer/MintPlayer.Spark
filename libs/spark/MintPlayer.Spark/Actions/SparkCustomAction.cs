using MintPlayer.Spark.Abstractions.Actions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Convenience base class for custom actions.
/// Developers can inherit from this OR implement ICustomAction directly.
/// In a future phase, Navigate/Notify helper methods will be added here,
/// powered by IManager (same mechanism as PersistentObject Actions classes).
/// </summary>
public abstract class SparkCustomAction : ICustomAction
{
    public abstract Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default);
}
