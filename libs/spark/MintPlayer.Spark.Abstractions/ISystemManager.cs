namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// <see cref="IManager.AsSystem"/>: persistent objects built whole, with no attribute removed for the
/// caller (D13a). Named so that every elevated construction can be found and reviewed.
/// </summary>
/// <remarks>
/// ⚠️ What this builds is not a presentation. Handing it to a client throws in Development and is
/// pruned in Production; a page goes through <see cref="IManager.GetPersistentObjectAsync(string, string, CancellationToken)"/>.
/// </remarks>
public interface ISystemManager
{
    /// <summary>As <see cref="IManager.GetPersistentObjectAsync(string, string, CancellationToken)"/>, whole.</summary>
    PersistentObject GetPersistentObject(string name);

    /// <summary>As <see cref="IManager.GetPersistentObjectAsync(Guid, string, CancellationToken)"/>, whole.</summary>
    PersistentObject GetPersistentObject(Guid id);

    /// <summary>As <see cref="IManager.GetPersistentObjectAsync{T}(string, CancellationToken)"/>, whole.</summary>
    PersistentObject GetPersistentObject<T>() where T : class;
}
