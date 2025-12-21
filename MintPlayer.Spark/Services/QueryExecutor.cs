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

[Register(typeof(IQueryExecutor), ServiceLifetime.Scoped, "AddSparkServices")]
internal partial class QueryExecutor : IQueryExecutor
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkContextResolver sparkContextResolver;

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

        // Find the entity type definition by CLR type
        var entityTypeDefinition = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
        if (entityTypeDefinition == null)
        {
            return [];
        }

        // Apply sorting if specified
        if (!string.IsNullOrEmpty(query.SortBy))
        {
            queryable = ApplySorting(queryable, entityType, query.SortBy, query.SortDirection);
        }

        // Execute the query
        var entities = await ExecuteQueryableAsync(queryable, entityType);

        // Convert to PersistentObjects
        return entities.Select(e => entityMapper.ToPersistentObject(e, entityTypeDefinition.Id));
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
