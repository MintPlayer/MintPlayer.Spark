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
        if (!string.IsNullOrEmpty(indexName))
        {
            queryable = ApplyIndex(session, entityType, resultType, indexName);
        }

        // Apply sorting if specified
        if (!string.IsNullOrEmpty(query.SortBy))
        {
            queryable = ApplySorting(queryable, resultType, query.SortBy, query.SortDirection);
        }

        // Execute the query
        var entities = await ExecuteQueryableAsync(queryable, resultType);

        // Convert to PersistentObjects using the entity type definition (which includes merged attributes)
        return entities.Select(e => entityMapper.ToPersistentObject(e, entityTypeDefinition.Id));
    }

    private object ApplyIndex(IAsyncDocumentSession session, Type entityType, Type resultType, string indexName)
    {
        // Use session.Query<T>(indexName) to query with a specific index
        // Find the extension method from LinqExtensions or use interface method
        var sessionQueryMethod = typeof(IAsyncDocumentSession).GetMethods()
            .FirstOrDefault(m => m.Name == "Query"
                && m.IsGenericMethod
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(string));

        if (sessionQueryMethod != null)
        {
            var genericSessionQueryMethod = sessionQueryMethod.MakeGenericMethod(resultType);
            // RavenDB converts underscores to slashes in index names (e.g., "People_Overview" -> "People/Overview")
            var ravenIndexName = indexName.Replace("_", "/");
            var query = genericSessionQueryMethod.Invoke(session, [ravenIndexName])!;

            // Use ProjectInto<T>() to project from stored fields in the index
            // This ensures computed/stored fields like FullName are populated from the index
            // ProjectInto is an extension method on IQueryable (non-generic) in LinqExtensions
            var projectIntoMethod = typeof(LinqExtensions).GetMethods()
                .FirstOrDefault(m => m.Name == "ProjectInto"
                    && m.IsGenericMethod
                    && m.GetGenericArguments().Length == 1
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(IQueryable));

            if (projectIntoMethod != null)
            {
                var genericProjectIntoMethod = projectIntoMethod.MakeGenericMethod(resultType);
                query = genericProjectIntoMethod.Invoke(null, [query])!;
            }

            return query;
        }

        // Fallback: use session.Query<T>() method directly (without index)
        var noIndexQueryMethod = typeof(IAsyncDocumentSession).GetMethods()
            .FirstOrDefault(m => m.Name == "Query"
                && m.IsGenericMethod
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 0);

        if (noIndexQueryMethod != null)
        {
            var genericQueryMethod = noIndexQueryMethod.MakeGenericMethod(resultType);
            return genericQueryMethod.Invoke(session, [])!;
        }

        throw new InvalidOperationException($"Could not apply index {indexName}");
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
