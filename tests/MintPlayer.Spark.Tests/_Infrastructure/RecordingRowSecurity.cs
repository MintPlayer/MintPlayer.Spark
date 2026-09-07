using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// A row-security double that behaves exactly like <see cref="PermissiveRowSecurity"/> — it keeps
/// every row — but <b>records what it was asked</b>.
/// <para>
/// This exists because the permissive double cannot fail. Deleting every
/// <c>FilterAsync</c>/<c>RedactAsync</c> call from an executor leaves a suite using
/// <see cref="PermissiveRowSecurity"/> entirely green, because "allow everything" and "never asked"
/// produce identical rows. That is the shape of a silent fail-open, and the executors most likely to
/// grow one — the custom-action path and the streaming path — are exactly the ones that install the
/// permissive double today.
/// </para>
/// <para>
/// Assert against <see cref="Calls"/> to pin that enforcement was <em>invoked</em>, with the right
/// type, result type and action. Use <see cref="DenyAllRowSecurity"/> to pin that its answer is
/// actually honoured.
/// </para>
/// </summary>
internal sealed class RecordingRowSecurity : IRowSecurity
{
    /// <summary>One entry per enforcement call, in order.</summary>
    /// <param name="Member">The <see cref="IRowSecurity"/> member invoked.</param>
    /// <param name="EntityType">The type the rule is written against.</param>
    /// <param name="ResultType">The materialized row type — differs from <paramref name="EntityType"/> for a projection.</param>
    /// <param name="Action">The verb: <c>Query</c> on list paths, <c>Read</c> on the detail path.</param>
    /// <param name="RowCount">How many rows were handed in, where the member takes rows.</param>
    internal sealed record Call(string Member, Type? EntityType, Type? ResultType, string? Action, int RowCount);

    private readonly List<Call> calls = [];

    /// <summary>Every call so far, in invocation order.</summary>
    public IReadOnlyList<Call> Calls => calls;

    /// <summary>Calls to one member, in order. Convenience for the common assertion.</summary>
    public IReadOnlyList<Call> CallsTo(string member)
        => [.. calls.Where(c => string.Equals(c.Member, member, StringComparison.Ordinal))];

    /// <summary>
    /// True when both halves of set enforcement ran. Filtering without redacting still leaks
    /// protected <em>values</em> on rows the caller may legitimately see, so neither alone is enough.
    /// </summary>
    public bool FilteredAndRedacted
        => CallsTo(nameof(FilterAsync)).Count > 0 && CallsTo(nameof(RedactAsync)).Count > 0;

    public void Clear() => calls.Clear();

    public Task<bool> IsAllowedAsync(Type entityType, string action, object entity)
    {
        calls.Add(new Call(nameof(IsAllowedAsync), entityType, null, action, 1));
        return Task.FromResult(true);
    }

    public Task<bool> AreAllowedAsync(IAsyncDocumentSession session, Type entityType, string action, IReadOnlyCollection<string> ids)
    {
        calls.Add(new Call(nameof(AreAllowedAsync), entityType, null, action, ids.Count));
        return Task.FromResult(true);
    }

    // Deliberately false, matching the permissive double: a recording run must not change which
    // branches the executor takes, or it would no longer be recording the real code path.
    public bool HasRowRule(Type entityType) => false;

    public Task<IReadOnlyList<object>> FilterAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<object> entities,
        Type entityType,
        Type resultType,
        string action,
        CancellationToken cancellationToken = default)
    {
        calls.Add(new Call(nameof(FilterAsync), entityType, resultType, action, entities.Count));
        return Task.FromResult(entities);
    }

    public Task<object> ComposeRowFilterAsync(object queryable, Type entityType, Type elementType, string action, CancellationToken cancellationToken = default)
    {
        calls.Add(new Call(nameof(ComposeRowFilterAsync), entityType, elementType, action, 0));
        return Task.FromResult(queryable);
    }

    public void ResetRequestFilterCache()
        => calls.Add(new Call(nameof(ResetRequestFilterCache), null, null, null, 0));

    public Task<LambdaExpression?> GetFilterExpressionAsync(Type entityType, string action)
    {
        calls.Add(new Call(nameof(GetFilterExpressionAsync), entityType, null, action, 0));
        return Task.FromResult<LambdaExpression?>(null);
    }

    public Task RedactAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<(PersistentObject Po, object Row)> items,
        Type entityType,
        Type resultType,
        string action,
        CancellationToken cancellationToken = default)
    {
        calls.Add(new Call(nameof(RedactAsync), entityType, resultType, action, items.Count));
        return Task.CompletedTask;
    }
}
