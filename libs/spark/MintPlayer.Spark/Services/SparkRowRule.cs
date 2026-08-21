using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions.Authorization;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The public face of <see cref="IRowSecurity"/>, closed over one entity type.
/// <para>
/// It is a thin delegation on purpose: every decision stays in <see cref="RowSecurity"/>, including
/// the per-request memo that bounds hook invocations to one per <c>(type, action)</c>. A second
/// implementation with its own memo would double hook invocations on any request that reached both
/// a controller and <c>/spark</c>, against RavenDB's 30-requests-per-session cap — the very cap the
/// memo exists to protect (#239). Sharing it is the reason this delegates rather than reimplements.
/// </para>
/// </summary>
/// <remarks>
/// Constructed by hand rather than through <c>[Inject]</c>/<c>[Register]</c>: both generators skip
/// open generic types, and an attribute that silently does nothing is worse than none — the class
/// would compile, resolve, and null-reference on first use. Registered explicitly in
/// <c>AddSparkCore</c> for the same reason.
/// </remarks>
internal sealed class SparkRowRule<T>(
    IRowSecurity rowSecurity,
    IActionsResolver actionsResolver,
    IAsyncDocumentSession session) : ISparkRowRule<T> where T : class
{
    public Task<IReadOnlyList<T>> ApplyAsync(
        IQueryable<T> query, string action, CancellationToken cancellationToken = default)
        => ApplyAsync<T>(query, action, cancellationToken);

    public async Task<IReadOnlyList<TResult>> ApplyAsync<TResult>(
        IQueryable<TResult> query, string action, CancellationToken cancellationToken = default)
        where TResult : class
    {
        // Pushdown first, so a scoped type reads its own rows rather than the whole collection. It
        // is an optimization only — ComposeRowFilterAsync declines silently for a projection or a
        // constant predicate, and FilterAsync below is what actually enforces the rule.
        var composed = (IQueryable<TResult>)await rowSecurity
            .ComposeRowFilterAsync(query, typeof(T), typeof(TResult), action);

        var rows = await MaterializeAsync(composed, cancellationToken);

        var visible = await rowSecurity.FilterAsync(
            session, rows, typeof(T), typeof(TResult), action);

        return [.. visible.Cast<TResult>()];
    }

    public Task<bool> IsAllowedAsync(string action, T entity, CancellationToken cancellationToken = default)
        => rowSecurity.IsAllowedAsync(typeof(T), action, entity);

    public Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(
        string action, T entity, CancellationToken cancellationToken = default)
        => actionsResolver.Resolve<T>().GetProtectedAttributesAsync(action, entity);

    public async Task<Expression<Func<T, bool>>?> GetFilterAsync(
        string action, CancellationToken cancellationToken = default)
        => (Expression<Func<T, bool>>?)await rowSecurity.GetFilterExpressionAsync(typeof(T), action);

    /// <summary>
    /// Materializes without deciding for the caller whether the query is RavenDB's. A RavenDB query
    /// goes over the wire and must be awaited; an in-memory one (a test double, a pre-materialized
    /// list) has no async form at all, and calling <c>ToListAsync</c> on it throws.
    /// </summary>
    private static async Task<IReadOnlyList<object>> MaterializeAsync<TResult>(
        IQueryable<TResult> query, CancellationToken cancellationToken)
        where TResult : class
    {
        if (query is IRavenQueryable<TResult> ravenQuery)
            return [.. await ravenQuery.ToListAsync(cancellationToken)];

        return [.. query];
    }
}
