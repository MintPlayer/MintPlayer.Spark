using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Abstractions;

public interface IManager
{
    /// <summary>
    /// Creates a virtual PersistentObject (not backed by a DB entity).
    /// Useful for building custom dialogs in Retry.Action().
    /// </summary>
    PersistentObject NewPersistentObject(string name, params PersistentObjectAttribute[] attributes);

    /// <summary>
    /// Access to the Retry Action subsystem.
    /// </summary>
    IRetryAccessor Retry { get; }
}
