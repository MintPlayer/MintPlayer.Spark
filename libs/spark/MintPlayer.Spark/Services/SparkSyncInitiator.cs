using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The request's <see cref="ISparkSyncInitiator"/>: set by <see cref="SyncActionHandler"/> around one sync
/// write, read by the stamping interceptors (#271, F2).
/// </summary>
[Register(typeof(ISparkSyncInitiator), ServiceLifetime.Scoped)]
internal sealed class SparkSyncInitiator : ISparkSyncInitiator
{
    public string? UserId { get; private set; }

    /// <summary>States <paramref name="userId"/> until the returned scope is disposed.</summary>
    public IDisposable Begin(string? userId)
    {
        var previous = UserId;
        UserId = userId;
        return new Restore(this, previous);
    }

    private sealed class Restore(SparkSyncInitiator owner, string? previous) : IDisposable
    {
        public void Dispose() => owner.UserId = previous;
    }
}
