using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Hierarchy;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.Hierarchy;

/// <summary>
/// RavenDB reads and writes over a <see cref="MaterializedPath"/> field (warehouse PRD WX9): a subtree as one
/// prefix query, and a subtree moved as one set-based patch.
/// </summary>
/// <remarks>
/// Every value (the prefix, the old and new paths) travels as a query parameter: the LINQ provider turns the
/// constants into <c>$p</c> parameters, and the patch passes <see cref="Parameters"/>. The two identifiers the
/// patch has to splice, collection and field, pass <see cref="RqlIdentifier"/> first.
/// <para>
/// ⚠️ RavenDB's <c>startsWith</c> on an auto-index compares case-insensitively. Two sibling segments that differ
/// only in case would both match a prefix query; the move patch re-checks the prefix in JavaScript with an
/// ordinal comparison, so it never rewrites a path that merely matched the index.
/// </para>
/// </remarks>
public static class MaterializedPathQueries
{
    /// <summary>The bound on <see cref="MoveSubtreeAsync(IDocumentStore, string, string, string, string?, TimeSpan?, CancellationToken)"/>'s wait.</summary>
    public static readonly TimeSpan DefaultMoveWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Narrows <paramref name="query"/> to the rows below <paramref name="ancestorPath"/> (and to the ancestor
    /// itself when <paramref name="includeSelf"/> is set), by the descendant prefix, so <c>A/1</c> never
    /// matches <c>A/10</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="ancestorPath"/> is malformed, or <paramref name="path"/> is not a member access.</exception>
    public static IRavenQueryable<T> WhereDescendantOf<T>(this IRavenQueryable<T> query, Expression<Func<T, string?>> path, string ancestorPath, bool includeSelf = false)
        => query.Where(DescendantPredicate(path, ancestorPath, includeSelf));

    /// <summary>
    /// The predicate behind <see cref="WhereDescendantOf{T}"/>, for a caller composing its own query (or an
    /// in-memory list, where it compares ordinally).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="ancestorPath"/> is malformed, or <paramref name="path"/> is not a member access.</exception>
    public static Expression<Func<T, bool>> DescendantPredicate<T>(Expression<Func<T, string?>> path, string ancestorPath, bool includeSelf = false)
    {
        if (path.Body is not MemberExpression member)
            throw new ArgumentException("The path selector must be a property access, such as x => x.Path.", nameof(path));

        var prefix = MaterializedPath.DescendantPrefix(ancestorPath);
        var startsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
        Expression body = Expression.AndAlso(
            Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
            Expression.Call(member, startsWith, Expression.Constant(prefix)));
        if (includeSelf)
            body = Expression.OrElse(Expression.Equal(member, Expression.Constant(ancestorPath, typeof(string))), body);
        return Expression.Lambda<Func<T, bool>>(body, path.Parameters);
    }

    /// <summary>
    /// Moves the subtree rooted at <paramref name="movedRootPath"/> under <paramref name="newParentPath"/>
    /// (<see langword="null"/>: it becomes a root) by rewriting the path field of the root and of every
    /// descendant in <typeparamref name="T"/>'s collection. Returns the number of documents patched.
    /// </summary>
    /// <remarks>
    /// Only the path is rewritten. The moved root's parent <em>reference</em> (what the hierarchical picker
    /// filters on, <see cref="SparkReferenceTree.ParentAttribute"/>) is the caller's to set, in the same unit
    /// of work that decided the move; its descendants' parents do not change.
    /// </remarks>
    /// <exception cref="ArgumentException">A path is malformed, the new parent lies inside the moved subtree, or <paramref name="path"/> is not a property access.</exception>
    /// <exception cref="TimeoutException">The patch outlived the wait. It keeps running on the server.</exception>
    public static Task<long> MoveSubtreeAsync<T>(IDocumentStore store, Expression<Func<T, string?>> path, string movedRootPath, string? newParentPath, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        if (path.Body is not MemberExpression { Expression: ParameterExpression } member)
            throw new ArgumentException("The path selector must be a top-level property access, such as x => x.Path.", nameof(path));
        return MoveSubtreeAsync(store, store.Conventions.GetCollectionName(typeof(T)), member.Member.Name, movedRootPath, newParentPath, wait, cancellationToken);
    }

    /// <summary>
    /// The untyped form of <see cref="MoveSubtreeAsync{T}"/>: <paramref name="collection"/> and
    /// <paramref name="pathField"/> are identifiers, validated by <see cref="RqlIdentifier"/> before they are
    /// spliced; every path is a parameter.
    /// </summary>
    /// <exception cref="ArgumentException">An identifier or path is malformed, or the new parent lies inside the moved subtree.</exception>
    /// <exception cref="TimeoutException">The patch outlived the wait. It keeps running on the server.</exception>
    public static async Task<long> MoveSubtreeAsync(IDocumentStore store, string collection, string pathField, string movedRootPath, string? newParentPath, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        var rql = BuildMoveRql(collection, pathField);
        var parameters = BuildMoveParameters(movedRootPath, newParentPath);
        var bound = wait ?? DefaultMoveWait;

        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(
                new IndexQuery { Query = rql, QueryParameters = parameters },
                // A node stored a moment ago may not be in the auto-index yet; without a stale timeout the
                // server refuses the whole patch ("Index is stale").
                new QueryOperationOptions { AllowStale = false, StaleTimeout = bound }),
            token: cancellationToken);
        var result = await operation.WaitForCompletionAsync<BulkOperationResult>(bound);
        return result.Total;
    }

    /// <summary>The move patch's RQL, with the identifiers validated and every value a parameter.</summary>
    /// <exception cref="ArgumentException">The collection or field is not a plain identifier, or the field steps into an array.</exception>
    internal static string BuildMoveRql(string collection, string pathField)
    {
        var from = RqlIdentifier.Collection(collection);
        var field = RqlIdentifier.FieldPath(pathField);
        if (field.Contains("[]", StringComparison.Ordinal))
            throw new ArgumentException($"'{pathField}' steps into an array; a materialized path is one string field.", nameof(pathField));

        // The JavaScript re-checks the match ordinally (startsWith on the index is case-insensitive), and
        // rewrites only the moved root's own prefix: the rest of each path is kept as is.
        return $"from {from} where {field} = $root or startsWith({field}, $prefix) "
             + $"update {{ var p = this.{field}; "
             + $"if (p === $root || (typeof p === 'string' && p.startsWith($prefix))) {{ this.{field} = $newRoot + p.substring($rootLength); }} }}";
    }

    /// <summary>The move patch's parameters.</summary>
    /// <exception cref="ArgumentException">A path is malformed, or the new parent lies inside the moved subtree.</exception>
    internal static Parameters BuildMoveParameters(string movedRootPath, string? newParentPath)
    {
        var newRoot = MaterializedPath.ReparentRoot(movedRootPath, newParentPath);
        return new Parameters
        {
            ["root"] = movedRootPath,
            ["prefix"] = MaterializedPath.DescendantPrefix(movedRootPath),
            ["newRoot"] = newRoot,
            ["rootLength"] = movedRootPath.Length,
        };
    }
}
