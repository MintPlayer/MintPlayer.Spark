using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Request-scoped coordination between <c>DatabaseAccess</c> and the base Actions hooks: the flag
/// that tells them to leave <c>SaveChanges</c> to the caller, so several rows are written by one
/// <c>SaveChanges</c> — the bulk delete's all-or-nothing guarantee (#460, D18) — and the change
/// vector a delete must still find (#467, D14).
/// </summary>
/// <remarks>
/// Internal on purpose: an application never opens a batch, and an override that calls
/// <c>SaveChangesAsync</c> itself is not stopped by it (that is the D1 override gap; the bulk delete
/// logs it). An <c>OnDeleteAsync</c> override that never reaches the base deletes without the
/// expected change vector; the etag check before it still refuses a stale request.
/// </remarks>
[Register(typeof(ISparkWriteBatch), ServiceLifetime.Scoped)]
internal sealed partial class SparkWriteBatch : ISparkWriteBatch
{
    private readonly Dictionary<string, string> expectedChangeVectors = new(StringComparer.OrdinalIgnoreCase);

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

    /// <inheritdoc />
    public void ExpectChangeVector(string id, string changeVector) => expectedChangeVectors[id] = changeVector;

    /// <inheritdoc />
    public string? TakeExpectedChangeVector(string id)
        => expectedChangeVectors.Remove(id, out var changeVector) ? changeVector : null;

    /// <inheritdoc />
    public void ForgetExpectedChangeVectors() => expectedChangeVectors.Clear();

    private sealed class Closer(SparkWriteBatch batch) : IDisposable
    {
        public void Dispose() => batch.IsDeferring = false;
    }
}

/// <summary>The contract of <see cref="SparkWriteBatch"/>, for its registration.</summary>
internal interface ISparkWriteBatch
{
    bool IsDeferring { get; }
    IDisposable Begin();

    /// <summary>
    /// The version the caller saw of the row about to be deleted (#467, D14): the base
    /// <c>OnDeleteAsync</c> deletes with it, so an edit landing after the etag check is a 409 too.
    /// </summary>
    void ExpectChangeVector(string id, string changeVector);

    /// <summary>The expected change vector for <paramref name="id"/>, once; null when none was set.</summary>
    string? TakeExpectedChangeVector(string id);

    /// <summary>Drops every expectation a refused delete left unconsumed.</summary>
    void ForgetExpectedChangeVectors();
}
