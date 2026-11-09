using System.Linq.Expressions;
using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary><see cref="ISparkQueryProvider"/> over the request's RavenDB session.</summary>
[Register(typeof(ISparkQueryProvider), ServiceLifetime.Scoped)]
internal sealed partial class RavenSparkQueryProvider : ISparkQueryProvider
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly IIndexCatalog indexCatalog;

    private IAsyncDocumentSession Session => session.AsRavenSession();

    public bool IsStoreQuery(IQueryable source)
        => typeof(IRavenQueryable<>).MakeGenericType(source.ElementType).IsInstanceOfType(source);

    public IQueryable OpenIndex(Type entityType, Type indexType, bool narrowToCollection)
        => narrowToCollection
            ? CollectionNarrowedIndex(entityType, indexType)
            : IndexWithType(entityType, indexType);

    /// <summary><c>session.Query&lt;resultType, indexType&gt;()</c>.</summary>
    private IQueryable IndexWithType(Type resultType, Type indexType)
    {
        var genericMethod = ReflectionCache.GetOrAdd<(string Op, Type Result, Type Index), MethodInfo>(
            ("RavenSparkQueryProvider.SessionQueryByIndexCreator", resultType, indexType),
            static k =>
            {
                var sessionQueryMethod = typeof(IAsyncDocumentSession).GetMethods()
                    .FirstOrDefault(m => m.Name == "Query"
                        && m.IsGenericMethod
                        && m.GetGenericArguments().Length == 2
                        && m.GetParameters().Length == 0)
                    ?? throw new InvalidOperationException("Could not find Query<T, TIndexCreator> method on IAsyncDocumentSession");
                return sessionQueryMethod.MakeGenericMethod(k.Result, k.Index);
            });
        return (IQueryable)genericMethod.Invoke(Session, [])!;
    }

    /// <summary>
    /// <c>session.Query&lt;SparkCollectionRow, TIndex&gt;().Where(r =&gt; r.SparkCollection == collection).OfType&lt;T&gt;()</c>:
    /// a multi-map default narrowed to <paramref name="entityType"/>'s collection, typed as
    /// <c>IRavenQueryable&lt;T&gt;</c> exactly like <see cref="IndexWithType"/>'s result, so everything
    /// downstream composes on it unchanged (SP1: correct rows, paging, totals and view projection, with and
    /// without a row policy).
    /// </summary>
    /// <remarks>
    /// The collection is a query parameter, never spliced RQL, and it is the store's own name for the type —
    /// the same convention that rendered <c>docs.&lt;Collection&gt;</c> in the map the discriminator was
    /// injected into (<see cref="SparkMultiMapIndexCreationTask{TReduceResult}.CreateIndexDefinition"/>).
    /// </remarks>
    private IQueryable CollectionNarrowedIndex(Type entityType, Type indexType)
    {
        var collection = session.GetCollectionName(entityType);
        var method = ReflectionCache.GetOrAdd<(string Op, Type Entity, Type Index), MethodInfo>(
            ("RavenSparkQueryProvider.CollectionNarrowedIndexQuery", entityType, indexType),
            static k => typeof(RavenSparkQueryProvider)
                .GetMethod(nameof(CollectionNarrowedIndexQuery), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(k.Entity, k.Index));
        return (IQueryable)method.Invoke(null, [Session, collection])!;
    }

    private static IRavenQueryable<T> CollectionNarrowedIndexQuery<T, TIndex>(IAsyncDocumentSession session, string collection)
        where TIndex : global::Raven.Client.Documents.Indexes.AbstractCommonApiForIndexes, new()
        => (IRavenQueryable<T>)session.Query<SparkCollectionRow, TIndex>()
            .Where(row => row.SparkCollection == collection)
            .OfType<T>();

    /// <summary><c>ProjectInto&lt;resultType&gt;()</c>: the index's stored and computed fields, not the source document's.</summary>
    public IQueryable Project(IQueryable source, Type resultType)
    {
        var genericProjectMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("RavenSparkQueryProvider.LinqProjectInto", resultType),
            static k =>
            {
                var projectIntoMethod = typeof(LinqExtensions).GetMethods()
                    .FirstOrDefault(m => m.Name == "ProjectInto"
                        && m.IsGenericMethod
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(IQueryable));
                return projectIntoMethod?.MakeGenericMethod(k.Type);
            });

        return genericProjectMethod is null ? source : (IQueryable)genericProjectMethod.Invoke(null, [source])!;
    }

    public IQueryable Include(IQueryable source, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return source;

        // RavenDB's Include on a queryable is the STATIC extension
        // LinqExtensions.Include<TResult>(IQueryable<TResult>, string) — there is NO instance
        // Include on RavenQueryInspector<T> (code that reflected for one silently no-oped, so Spark
        // applied no includes at all). Cached per element type.
        var includeMethod = ReflectionCache.GetOrAdd<(string Op, Type Element), MethodInfo>(
            ("RavenSparkQueryProvider.LinqInclude", source.ElementType),
            static k => typeof(LinqExtensions).GetMethods()
                .First(m => m.Name == "Include"
                    && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters() is { Length: 2 } ps
                    && ps[0].ParameterType.IsGenericType
                    && ps[0].ParameterType.GetGenericTypeDefinition() == typeof(IQueryable<>)
                    && ps[1].ParameterType == typeof(string))
                .MakeGenericMethod(k.Element));

        foreach (var path in paths)
            source = (IQueryable)includeMethod.Invoke(null, [source, path])!;

        return source;
    }

    /// <remarks>
    /// <c>x.Id.In(ids)</c>, RavenDB's own marker method: its translator rejects the <c>ids.Contains(x.Id)</c> form
    /// LINQ-to-objects needs.
    /// </remarks>
    public Expression In(Expression member, IReadOnlyList<string> values)
        => Expression.Call(InMethod, member, Expression.Constant(values.ToList()));

    private static readonly MethodInfo InMethod = typeof(RavenQueryableExtensions)
        .GetMethods()
        .First(m => m.Name == "In"
                 && m.GetParameters().Length == 2
                 && m.GetParameters()[1].ParameterType.IsGenericType)
        .MakeGenericMethod(typeof(string));

    /// <remarks>
    /// Null for a dynamic query: its auto-index is built from the query's own shape, so it cannot name a field
    /// it does not index. A static index with a <c>[FromIndex]</c> projection declares its fields through the
    /// projection's properties. One without a projection holds its field set only inside the Map, as a rendered
    /// string, so the set is empty: unknowable, never guessed.
    /// </remarks>
    public IReadOnlySet<string>? IndexedFields(IQueryable source)
    {
        if (source is not IRavenQueryInspector inspector) return null;

        var indexName = inspector.IndexName;
        if (string.IsNullOrEmpty(indexName)) return null;

        // An auto-index is RavenDB's own, built per query shape; it is never in our catalog and never
        // rejects a field the query names.
        if (indexName.StartsWith("Auto/", StringComparison.Ordinal)) return null;

        // The catalog keys on the CLR class name while RavenDB reports the deployed name, which
        // replaces '_' with '/'. Unambiguous to reverse: a CLR type name cannot contain '/'.
        var entry = indexCatalog.GetByIndexName(indexName.Replace('/', '_'));
        if (entry?.ProjectionType is null) return EmptyFieldSet;

        return ReflectionCache.GetOrAdd<(string Op, Type Projection), IReadOnlySet<string>>(
            ("RavenSparkQueryProvider.IndexedSearchFields", entry.ProjectionType),
            static k => k.Projection.GetCachedProperties()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal));
    }

    private static readonly IReadOnlySet<string> EmptyFieldSet = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One <c>Search</c> clause per property.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="SearchOptions"/> is never passed explicitly. That is a safety requirement rather than a style
    /// preference: measured on RavenDB 7.2.5, an explicit <see cref="SearchOptions.Or"/> leaks onto the
    /// <em>adjacent</em> clause and ORs it in. The adjacent clause is the row-security predicate, so an explicit
    /// option would turn a security filter into an alternative — silently, returning plausible rows. The default
    /// <see cref="SearchOptions.Guess"/> instead groups the consecutive clauses and ANDs the group with its
    /// neighbours in both directions, which is exactly what a multi-field search wants. The caller keeps the
    /// security predicate directly ahead of this group.
    /// </para>
    /// <para>Every argument is passed because <see cref="MethodInfo.Invoke"/> does not apply optional parameter defaults.</para>
    /// </remarks>
    public IQueryable? Search(IQueryable source, IReadOnlyList<PropertyInfo> properties, string term)
    {
        var elementType = source.ElementType;
        var searchMethod = ReflectionCache.GetOrAdd<(string Op, Type Element), MethodInfo>(
            ("RavenSparkQueryProvider.LinqSearch", elementType),
            static k => typeof(LinqExtensions).GetMethods()
                .First(m => m.Name == nameof(LinqExtensions.Search)
                    && m.IsGenericMethod
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 6
                    // The string overload, not the IEnumerable<string> one.
                    && m.GetParameters()[2].ParameterType == typeof(string))
                .MakeGenericMethod(k.Element));

        var selectorType = typeof(Func<,>).MakeGenericType(elementType, typeof(object));

        foreach (var property in properties)
        {
            var parameter = Expression.Parameter(elementType, "x");
            // Expression<Func<T, object>>, so the property access needs boxing even for a string.
            var propertyAccess = Expression.Convert(Expression.Property(parameter, property), typeof(object));
            var lambda = Expression.Lambda(selectorType, propertyAccess, parameter);

            source = (IQueryable)searchMethod.Invoke(
                null,
                [source, lambda, term, 1m, SearchOptions.Guess, SearchOperator.And])!;
        }

        return source;
    }

    public async Task<int> CountAsync(IQueryable source, CancellationToken cancellationToken)
    {
        var method = ReflectionCache.GetOrAdd<(string Op, Type Entity), MethodInfo?>(
            ("RavenSparkQueryProvider.LinqCountAsync", source.ElementType),
            static k => typeof(LinqExtensions).GetMethods()
                .FirstOrDefault(m => m.Name == nameof(LinqExtensions.CountAsync)
                    && m.GetParameters().Length == 2)
                ?.MakeGenericMethod(k.Entity))
            ?? throw new InvalidOperationException("RavenDB's CountAsync could not be resolved.");

        return await (Task<int>)method.Invoke(null, [source, cancellationToken])!;
    }

    public async Task<IReadOnlyList<object>> ToListAsync(IQueryable source, CancellationToken cancellationToken)
    {
        var method = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("RavenSparkQueryProvider.LinqToListAsync", source.ElementType),
            static k => typeof(LinqExtensions).GetMethods()
                .FirstOrDefault(m => m.Name == nameof(LinqExtensions.ToListAsync)
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 2)
                ?.MakeGenericMethod(k.Type))
            ?? throw new InvalidOperationException("RavenDB's ToListAsync could not be resolved.");

        var task = (Task)method.Invoke(null, [source, cancellationToken])!;
        await task;
        return task.GetCompletedTaskResult() is System.Collections.IEnumerable rows ? [.. rows.Cast<object>()] : [];
    }
}
