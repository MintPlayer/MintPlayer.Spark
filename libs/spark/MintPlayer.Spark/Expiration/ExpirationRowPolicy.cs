using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Expiration;

/// <summary>
/// Hides every <see cref="IExpiring"/> row whose <c>ExpiresAtUtc</c> has passed, on every path that asks
/// row security — list, detail, custom queries, sub-queries, distinct values, streams, breadcrumbs,
/// reference pickers, the edit/delete gates and custom-action selections. A load of an expired row is
/// therefore the missing-row 404 (#453).
/// </summary>
/// <remarks>
/// <para>
/// Needed because deletion lags: RavenDB sweeps at most every 36 hours on the Community licence, so a
/// document can outlive its <c>@expires</c> by that much. The filter, not the sweep, is the guarantee.
/// </para>
/// <para>
/// ⚠️ <c>!(x.ExpiresAtUtc &lt;= now)</c>, never <c>x.ExpiresAtUtc == null || x.ExpiresAtUtc &gt; now</c>:
/// a document stored before its type implemented <see cref="IExpiring"/> has no field, and an absent
/// field matches no <c>== null</c> (measured, <c>AbsentVersusNullFieldTests</c>), so it would vanish.
/// The negated range matches absent, null and future alike (S6).
/// </para>
/// <para>
/// Applies in the system context too: background work must not act on a document that is, for every
/// purpose but storage, already gone. Not a visibility decision: it hides rows from everyone.
/// </para>
/// </remarks>
internal sealed partial class ExpirationRowPolicy : RowFilterPolicy<IExpiring>
{
    [Inject] private readonly TimeProvider? timeProvider;

    /// <inheritdoc />
    public override bool BypassInSystemContext => false;

    /// <inheritdoc />
    public override ValueTask<Expression<Func<IExpiring, bool>>?> GetFilterAsync(RowPolicyContext context)
        => ValueTask.FromResult<Expression<Func<IExpiring, bool>>?>(Live((timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime));

    /// <summary>Not expired at <paramref name="nowUtc"/>: no expiry, an absent field, or one still ahead.</summary>
    internal static Expression<Func<IExpiring, bool>> Live(DateTime nowUtc) => x => !(x.ExpiresAtUtc <= nowUtc);
}
