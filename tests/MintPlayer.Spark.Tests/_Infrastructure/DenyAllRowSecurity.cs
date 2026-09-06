using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// A row-security double that refuses everything: <see cref="FilterAsync"/> returns no rows and
/// <see cref="RedactAsync"/> blanks every attribute it is handed.
/// <para>
/// The companion to <see cref="RecordingRowSecurity"/>, and it answers the other half of the
/// question. Recording proves enforcement was <em>invoked</em>; this proves its answer is
/// <em>honoured</em> — that an executor does not, say, call <c>FilterAsync</c> and then project from
/// the pre-filter collection it still has in scope. That mistake is invisible to a permissive double
/// and invisible to a recording one, because both return the rows unchanged.
/// </para>
/// <para>
/// The assertion it enables is deliberately blunt: <b>zero rows reach the wire</b>. Any executor for
/// which that does not hold is leaking, whatever else its tests say.
/// </para>
/// </summary>
internal sealed class DenyAllRowSecurity : IRowSecurity
{
    public Task<bool> IsAllowedAsync(Type entityType, string action, object entity) => Task.FromResult(false);

    public Task<bool> AreAllowedAsync(IAsyncDocumentSession session, Type entityType, string action, IReadOnlyCollection<string> ids)
        => Task.FromResult(false);

    /// <summary>
    /// True — unlike the permissive doubles. A denying rule that claimed to have no rule would let
    /// executors take their "nothing to enforce" fast paths and never reach the refusal.
    /// </summary>
    public bool HasRowRule(Type entityType) => true;

    public Task<IReadOnlyList<object>> FilterAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<object> entities,
        Type entityType,
        Type resultType,
        string action,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<object>>([]);

    /// <summary>
    /// Left as a no-op on purpose. Pushdown is an optimization, not the gate — <c>FilterAsync</c> is
    /// — so composing here would let a test pass because of the fast path rather than the gate, and
    /// hide the very bypass this double exists to catch.
    /// </summary>
    public Task<object> ComposeRowFilterAsync(object queryable, Type entityType, Type elementType, string action, CancellationToken cancellationToken = default)
        => Task.FromResult(queryable);

    public void ResetRequestFilterCache() { }

    public Task<LambdaExpression?> GetFilterExpressionAsync(Type entityType, string action)
        => Task.FromResult<LambdaExpression?>(null);

    /// <summary>
    /// Blanks every attribute on every row handed in. Reached only when an executor projects rows
    /// that <see cref="FilterAsync"/> already removed — which is itself the finding.
    /// </summary>
    public Task RedactAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<(PersistentObject Po, object Row)> items,
        Type entityType,
        Type resultType,
        string action,
        CancellationToken cancellationToken = default)
    {
        foreach (var (po, _) in items)
            foreach (var attribute in po.Attributes)
            {
                attribute.Value = null;
                attribute.Breadcrumb = null;
                attribute.IsVisible = false;
            }

        return Task.CompletedTask;
    }
}
