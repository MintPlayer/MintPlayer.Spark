using System.Linq.Expressions;
using System.Reflection;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// The provider's half of a Spark query (docs/spark_editor_PRD.md §4b, S3): where rows come from and how a
/// query is executed. Spark's query pipeline owns everything else, as LINQ expression trees composed on the
/// queryable — the row filter, column filters, restrict-to-ids and sorting — plus the paging decision and the
/// row-security gate on materialized rows, which is the enforcement point whatever a provider does here.
/// </summary>
/// <remarks>
/// A query reaching the pipeline is either the store's own (<see cref="IsStoreQuery"/>: it translates and
/// executes remotely) or an in-memory sequence an actions method returned. Every hook that only makes sense for
/// the store's own queries is called only for them.
/// </remarks>
public interface ISparkQueryProvider
{
    /// <summary>Whether <paramref name="source"/> is this store's query rather than an in-memory sequence.</summary>
    bool IsStoreQuery(IQueryable source);

    /// <summary>
    /// The query over a declared index (<paramref name="indexType"/>, from the index catalog) yielding
    /// <paramref name="entityType"/> rows. <paramref name="narrowToCollection"/>: the index maps several
    /// collections and must be narrowed to <paramref name="entityType"/>'s.
    /// </summary>
    IQueryable OpenIndex(Type entityType, Type indexType, bool narrowToCollection);

    /// <summary>
    /// <paramref name="source"/> read as <paramref name="resultType"/>: the index's stored view rather than the
    /// documents behind it. Returns <paramref name="source"/> unchanged when the provider has nothing to project.
    /// </summary>
    IQueryable Project(IQueryable source, Type resultType);

    /// <summary>Loads the documents at <paramref name="paths"/> with the query's rows, when the provider can.</summary>
    IQueryable Include(IQueryable source, IReadOnlyCollection<string> paths);

    /// <summary>
    /// <c>member ∈ values</c> in the form the provider translates. LINQ-to-objects takes
    /// <c>values.Contains(member)</c>; a store's translator may need its own operator.
    /// </summary>
    Expression In(Expression member, IReadOnlyList<string> values);

    /// <summary>
    /// The fields the index behind <paramref name="source"/> lets a query name. Null: unrestricted (no index, or
    /// one built from the query's shape). Empty: an index is bound whose fields cannot be known.
    /// </summary>
    IReadOnlySet<string>? IndexedFields(IQueryable source);

    /// <summary>
    /// Full-text search for <paramref name="term"/> over <paramref name="properties"/>, ANDed with what
    /// <paramref name="source"/> already filters. Null when the provider has no full-text search; Spark then
    /// narrows the materialized rows itself.
    /// </summary>
    IQueryable? Search(IQueryable source, IReadOnlyList<PropertyInfo> properties, string term);

    /// <summary>Counts a store query's rows.</summary>
    Task<int> CountAsync(IQueryable source, CancellationToken cancellationToken);

    /// <summary>Executes a store query.</summary>
    Task<IReadOnlyList<object>> ToListAsync(IQueryable source, CancellationToken cancellationToken);
}
