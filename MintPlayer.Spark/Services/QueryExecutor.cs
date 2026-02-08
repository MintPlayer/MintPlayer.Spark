using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

public interface IQueryExecutor
{
    Task<IEnumerable<PersistentObject>> ExecuteQueryAsync(SparkQuery query);
}

[Register(typeof(IQueryExecutor), ServiceLifetime.Scoped)]
internal partial class QueryExecutor : IQueryExecutor
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkContextResolver sparkContextResolver;
    [Inject] private readonly IIndexRegistry indexRegistry;

    public async Task<IEnumerable<PersistentObject>> ExecuteQueryAsync(SparkQuery query)
    {
        using var session = documentStore.OpenAsyncSession();

        // Get the SparkContext with session injected
        var sparkContext = sparkContextResolver.ResolveContext(session);
        if (sparkContext == null)
        {
            return [];
        }

        // Get the property from the SparkContext
        var contextType = sparkContext.GetType();
        var property = contextType.GetProperty(query.ContextProperty, BindingFlags.Public | BindingFlags.Instance);

        if (property == null)
        {
            return [];
        }

        // Get the queryable from the property
        var queryable = property.GetValue(sparkContext);
        if (queryable == null)
        {
            return [];
        }

        // Determine the entity type from the IRavenQueryable<T>
        var queryableType = property.PropertyType;
        var entityType = queryableType.GetGenericArguments().FirstOrDefault();
        if (entityType == null)
        {
            return [];
        }

        // Get entity type definition
        var entityTypeDefinition = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
        if (entityTypeDefinition == null)
        {
            return [];
        }

        // Determine result type and index to use
        Type resultType = entityType;
        string? indexName = query.IndexName; // Use query-specified index first

        // Check IndexRegistry for projection type (from FromIndexAttribute on projections)
        var registration = indexRegistry.GetRegistrationForCollectionType(entityType);
        if (registration?.ProjectionType != null)
        {
            resultType = registration.ProjectionType;
            if (string.IsNullOrEmpty(indexName))
            {
                indexName = registration.IndexName;
            }
        }

        // Apply index if we have one (either from IndexRegistry or explicitly specified)
        Type? indexType = registration?.IndexType;
        if (!string.IsNullOrEmpty(indexName) && indexType != null)
        {
            // Use session.Query<TEntity, TIndexCreator>() with the entity type, then
            // ProjectInto to get stored/computed fields from the index.
            // Important: we query with entityType (not resultType) so that ProjectInto
            // is the single projection step — using resultType here would create a
            // double projection that causes duplicate results when combined with OrderBy.
            queryable = ApplyIndexWithType(session, entityType, indexType);
        }
        else if (!string.IsNullOrEmpty(indexName))
        {
            // Fallback: use string-based index name, queries as entityType
            queryable = ApplyIndexByName(session, entityType, indexName);
        }

        // Apply ProjectInto to get computed/stored fields from the index (e.g. FullName)
        if (!string.IsNullOrEmpty(indexName) && resultType != entityType)
        {
            queryable = ApplyProjection(queryable, resultType);
        }

        // Apply sorting after projection so it can operate on projected fields (e.g. FullName)
        var sortType = (indexType != null && resultType != entityType) ? resultType : entityType;
        if (!string.IsNullOrEmpty(query.SortBy))
        {
            queryable = ApplySorting(queryable, sortType, query.SortBy, query.SortDirection);
        }

        // Execute the query
        var entities = await ExecuteQueryableAsync(queryable, resultType);

        // Convert to PersistentObjects using the entity type definition (which includes merged attributes)
        // Deduplicate by ID: ProjectInto on indexes with FieldIndexing.Search can return
        // one result per search token for the same document. DistinctBy preserves sort order.
        return entities
            .Select(e => entityMapper.ToPersistentObject(e, entityTypeDefinition.Id))
            .DistinctBy(po => po.Id);
    }

    private object ApplyIndexWithType(IAsyncDocumentSession session, Type resultType, Type indexType)
    {
        // Use session.Query<TResult, TIndexCreator>() - the 2-generic-parameter, 0-regular-parameter overload
        var sessionQueryMethod = typeof(IAsyncDocumentSession).GetMethods()
            .FirstOrDefault(m => m.Name == "Query"
                && m.IsGenericMethod
                && m.GetGenericArguments().Length == 2
                && m.GetParameters().Length == 0);

        if (sessionQueryMethod == null)
        {
            throw new InvalidOperationException("Could not find Query<T, TIndexCreator> method on IAsyncDocumentSession");
        }

        var genericMethod = sessionQueryMethod.MakeGenericMethod(resultType, indexType);
        return genericMethod.Invoke(session, [])!;
    }

    private object ApplyIndexByName(IAsyncDocumentSession session, Type entityType, string indexName)
    {
        // Fallback: session.Query<T>(indexName, collectionName, isMapReduce) - string-based
        var sessionQueryMethod = typeof(IAsyncDocumentSession).GetMethods()
            .FirstOrDefault(m => m.Name == "Query"
                && m.IsGenericMethod
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 3
                && m.GetParameters()[0].ParameterType == typeof(string)
                && m.GetParameters()[1].ParameterType == typeof(string)
                && m.GetParameters()[2].ParameterType == typeof(bool));

        if (sessionQueryMethod == null)
        {
            throw new InvalidOperationException("Could not find Query<T>(string, string, bool) method on IAsyncDocumentSession");
        }

        var genericMethod = sessionQueryMethod.MakeGenericMethod(entityType);
        var ravenIndexName = indexName.Replace("_", "/");
        return genericMethod.Invoke(session, [ravenIndexName, null, false])!;
    }

    private object ApplyProjection(object queryable, Type resultType)
    {
        var projectIntoMethod = typeof(LinqExtensions).GetMethods()
            .FirstOrDefault(m => m.Name == "ProjectInto"
                && m.IsGenericMethod
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(IQueryable));

        if (projectIntoMethod == null)
        {
            return queryable;
        }

        var genericProjectMethod = projectIntoMethod.MakeGenericMethod(resultType);
        return genericProjectMethod.Invoke(null, [queryable])!;
    }

    private object ApplySorting(object queryable, Type entityType, string sortBy, string? sortDirection)
    {
        // Get the property to sort by
        var propertyInfo = entityType.GetProperty(sortBy, BindingFlags.Public | BindingFlags.Instance);
        if (propertyInfo == null)
        {
            return queryable;
        }

        var isDescending = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        // Build lambda expression for OrderBy: x => x.SortByProperty
        var parameter = System.Linq.Expressions.Expression.Parameter(entityType, "x");
        var propertyAccess = System.Linq.Expressions.Expression.Property(parameter, propertyInfo);
        var lambda = System.Linq.Expressions.Expression.Lambda(propertyAccess, parameter);

        // Get the OrderBy or OrderByDescending method
        var methodName = isDescending ? "OrderByDescending" : "OrderBy";

        // For IRavenQueryable, we need to use the Queryable extension methods
        var orderByMethod = typeof(Queryable).GetMethods()
            .First(m => m.Name == methodName && m.GetParameters().Length == 2)
            .MakeGenericMethod(entityType, propertyInfo.PropertyType);

        return orderByMethod.Invoke(null, [queryable, lambda])!;
    }

    private async Task<IEnumerable<object>> ExecuteQueryableAsync(object queryable, Type entityType)
    {
        // Call ToListAsync on the IRavenQueryable<T>
        var toListMethod = typeof(LinqExtensions).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(LinqExtensions.ToListAsync)
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 2);

        if (toListMethod == null)
        {
            return [];
        }

        var genericToListMethod = toListMethod.MakeGenericMethod(entityType);
        var task = genericToListMethod.Invoke(null, [queryable, CancellationToken.None]) as Task;

        if (task == null)
        {
            return [];
        }

        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        var result = resultProperty?.GetValue(task);

        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToList();
        }

        return [];
    }
}
