using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// A row-security double that allows everything, for tests whose subject is not row-level
/// authorization.
/// <para>
/// A plain NSubstitute mock is wrong here: <c>FilterAsync</c> would return a null task and the
/// test would fail with a NullReferenceException far from the cause. This states the intent —
/// "row security is not what this test is about" — and keeps the rows flowing.
/// </para>
/// <para>
/// Tests that <em>are</em> about row-level authorization use the real <see cref="RowSecurity"/>
/// against real Actions classes; see <c>RowLevelQueryAuthorizationTests</c>.
/// </para>
/// </summary>
internal sealed class PermissiveRowSecurity : IRowSecurity
{
    public Task<bool> IsAllowedAsync(Type entityType, string action, object entity) => Task.FromResult(true);

    public bool HasRowRule(Type entityType) => false;

    public Task<IReadOnlyList<object>> FilterAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<object> entities,
        Type entityType,
        Type resultType,
        string action) => Task.FromResult(entities);

    public Task<object> ComposeRowFilterAsync(object queryable, Type entityType, Type elementType, string action)
        => Task.FromResult(queryable);

    public void ResetRequestFilterCache() { }

    public Task RedactAsync(
        IAsyncDocumentSession session,
        IReadOnlyList<(MintPlayer.Spark.Abstractions.PersistentObject Po, object Row)> items,
        Type entityType,
        Type resultType,
        string action) => Task.CompletedTask;
}
