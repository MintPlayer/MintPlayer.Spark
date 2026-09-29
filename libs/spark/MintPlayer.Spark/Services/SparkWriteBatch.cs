using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Request-scoped flag that tells the base Actions hooks to leave <c>SaveChanges</c> to the caller,
/// so several rows are written by one <c>SaveChanges</c> — the bulk delete's all-or-nothing
/// guarantee (#460, D18).
/// </summary>
/// <remarks>
/// Internal on purpose: an application never opens a batch, and an override that calls
/// <c>SaveChangesAsync</c> itself is not stopped by it (that is the D1 override gap; the bulk delete
/// logs it).
/// </remarks>
[Register(typeof(SparkWriteBatch), ServiceLifetime.Scoped)]
internal sealed class SparkWriteBatch
{
    /// <summary>Whether a batch is open, and the base hooks must not save.</summary>
    public bool IsDeferring { get; private set; }

    /// <summary>Opens the batch; disposing closes it. Not re-entrant.</summary>
    public IDisposable Begin()
    {
        if (IsDeferring)
            throw new InvalidOperationException("A write batch is already open in this request.");
        IsDeferring = true;
        return new Closer(this);
    }

    private sealed class Closer(SparkWriteBatch batch) : IDisposable
    {
        public void Dispose() => batch.IsDeferring = false;
    }
}
