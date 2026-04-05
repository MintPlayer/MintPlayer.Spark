using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Storage;
using System.Collections.Concurrent;
using System.Reflection;

namespace MintPlayer.Spark.Services;

public interface IQueryExecutor
{
    Task<QueryResult> ExecuteQueryAsync(SparkQuery query, PersistentObject? parent = null, int skip = 0, int take = 50, string? search = null);
}

[Register(typeof(IQueryExecutor), ServiceLifetime.Scoped)]
internal partial class QueryExecutor : IQueryExecutor
{
    [Inject] private readonly ISparkSessionFactory sessionFactory;
    [Inject] private readonly ISparkStorageProvider storageProvider;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkContextResolver sparkContextResolver;
    [Inject] private readonly IIndexRegistry indexRegistry;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IReferenceResolver referenceResolver;

    private static readonly ConcurrentDictionary<string, CustomQueryMethodInfo?> customQueryMethodCache = new();

    public async Task<QueryResult> ExecuteQueryAsync(SparkQuery query, PersistentObject? parent = null, int skip = 0, int take = 50, string? search = null)
    {
        var (isCustom, name) = ResolveSource(query);

        IEnumerable<PersistentObject> allResults;
        if (isCustom)
        {
            allResults = await ExecuteCustomQueryAsync(query, name, parent);
        }
        else
        {
            allResults = await ExecuteDatabaseQueryAsync(query, name);
        }

        // Apply server-side search filtering
        if (!string.IsNullOrEmpty(search))
        {
            var term = search.ToLowerInvariant();
            allResults = allResults.Where(po =>
                (po.Name != null && po.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (po.Breadcrumb != null && po.Breadcrumb.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                po.Attributes.Any(attr =>
                {
                    var value = attr.Breadcrumb ?? attr.Value?.ToString();
                    return value != null && value.Contains(term, StringComparison.OrdinalIgnoreCase);
                })
            ).ToList();
        }

        var materialized = allResults as IList<PersistentObject> ?? allResults.ToList();
        var totalRecords = materialized.Count;

        var paged = materialized.Skip(skip).Take(take);

        return new QueryResult
        {
            Data = paged,
            TotalRecords = totalRecords,
            Skip = skip,
            Take = take,
        };
    }

    private static (bool IsCustom, string Name) ResolveSource(SparkQuery query)
    {
        var source = query.Source;

        if (source.StartsWith("Custom.", StringComparison.OrdinalIgnoreCase))
            return (true, source[7..]);

        if (source.StartsWith("Database.", StringComparison.OrdinalIgnoreCase))
            return (false, source[9..]);

        throw new InvalidOperationException(
            $"Query '{query.Name}' has invalid Source '{query.Source}'. " +
            "Expected 'Database.PropertyName' or 'Custom.MethodName'.");
    }

    #region Database Queries

    private async Task<IEnumerable<PersistentObject>> ExecuteDatabaseQueryAsync(SparkQuery query, string propertyName)
    {
        using var session = sessionFactory.OpenSession();

        var sparkContext = sparkContextResolver.ResolveContext(session);
        if (sparkContext == null)
        {
            return [];
        }

        var contextType = sparkContext.GetType();
        var property = contextType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

        if (property == null)
        {
            return [];
        }

        var queryable = property.GetValue(sparkContext);
        if (queryable == null)
        {
            return [];
        }

        var queryableType = property.PropertyType;
        var entityType = queryableType.GetGenericArguments().FirstOrDefault();
        if (entityType == null)
        {
            return [];
        }

        var entityTypeDefinition = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
        if (entityTypeDefinition == null)
        {
            return [];
        }

        await permissionService.EnsureAuthorizedAsync("Query", entityTypeDefinition.Name);

        Type resultType = entityType;
        string? indexName = query.IndexName;

        var registration = indexRegistry.GetRegistrationForCollectionType(entityType);
        if (registration?.ProjectionType != null)
        {
            resultType = registration.ProjectionType;
            if (string.IsNullOrEmpty(indexName))
            {
                indexName = registration.IndexName;
            }
        }

        if (registration?.IndexType != null && resultType != entityType)
        {
            // Query<T>(Type indexType) returns stored/computed index fields via ProjectInto
            queryable = ApplyIndexByType(session, resultType, registration.IndexType);
        }
        else if (!string.IsNullOrEmpty(indexName))
        {
            queryable = ApplyIndexByName(session, entityType, indexName);
        }

        // Resolve reference properties before executing so we can chain .Include()
        var referenceProperties = referenceResolver.GetReferenceProperties(resultType, entityType);
        if (referenceProperties.Count > 0)
        {
            queryable = referenceResolver.ApplyIncludes(queryable, referenceProperties);
        }

        var sortType = (!string.IsNullOrEmpty(indexName) && resultType != entityType) ? resultType : entityType;
        if (query.SortColumns.Length > 0)
        {
            queryable = ApplySorting(queryable, sortType, query.SortColumns);
        }

        var entities = (await ExecuteQueryableAsync(queryable, resultType)).ToList();

        // Referenced docs are now in session cache — no extra DB calls
        var includedDocuments = await referenceResolver.ResolveReferencedDocumentsAsync(session, entities, referenceProperties);

        return entities
            .Select(e => entityMapper.ToPersistentObject(e, entityTypeDefinition.Id, includedDocuments))
            .DistinctBy(po => po.Id);
    }

    #endregion

    #region Custom Queries

    private async Task<IEnumerable<PersistentObject>> ExecuteCustomQueryAsync(
        SparkQuery query, string methodName, PersistentObject? parent)
    {
        using var session = sessionFactory.OpenSession();

        // Resolve the entity type for this query
        var entityTypeDefinition = ResolveEntityTypeDefinition(query, methodName);
        if (entityTypeDefinition == null)
        {
            return [];
        }

        await permissionService.EnsureAuthorizedAsync("Query", entityTypeDefinition.Name);

        // Resolve the entity CLR type
        var entityType = FindClrType(entityTypeDefinition.ClrType);
        if (entityType == null)
        {
            return [];
        }

        // Resolve the Actions class for this entity type
        var actionsInstance = actionsResolver.ResolveForType(entityType);

        // Find the custom query method
        var methodInfo = ResolveCustomQueryMethod(actionsInstance.GetType(), methodName);
        if (methodInfo == null)
        {
            throw new InvalidOperationException(
                $"Custom query method '{methodName}' not found on actions class '{actionsInstance.GetType().Name}'. " +
                $"Expected a public method returning IQueryable<T> with zero parameters or one CustomQueryArgs parameter.");
        }

        // Build args and invoke
        var parentTypeName = parent != null
            ? modelLoader.GetEntityType(parent.ObjectTypeId)?.Name
            : null;
        var args = new CustomQueryArgs
        {
            Parent = parent,
            ParentType = parentTypeName,
            Query = query,
            Session = session,
        };

        object? result;
        if (methodInfo.AcceptsArgs)
        {
            result = methodInfo.Method.Invoke(actionsInstance, [args]);
        }
        else
        {
            result = methodInfo.Method.Invoke(actionsInstance, []);
        }

        if (result == null)
        {
            return [];
        }

        // Apply index projection for computed/stored fields (e.g., FullName from People_Overview).
        // Without this, the storage provider loads full documents which lack computed index fields.
        if (methodInfo.IsQueryable && indexRegistry.IsProjectionType(methodInfo.ResultElementType))
        {
            result = storageProvider.ApplyProjection(result, methodInfo.ResultElementType);
        }

        // Apply sorting if the result is IQueryable
        if (methodInfo.IsQueryable && query.SortColumns.Length > 0)
        {
            result = ApplySorting(result, methodInfo.ResultElementType, query.SortColumns);
        }

        // Materialize results
        IEnumerable<object> entities;
        if (methodInfo.IsQueryable)
        {
            entities = await ExecuteQueryableAsync(result, methodInfo.ResultElementType);
        }
        else if (result is System.Collections.IEnumerable enumerable)
        {
            entities = enumerable.Cast<object>().ToList();
        }
        else
        {
            return [];
        }

        // Resolve reference breadcrumbs
        var referenceProperties = referenceResolver.GetReferenceProperties(methodInfo.ResultElementType);
        var includedDocuments = await referenceResolver.ResolveReferencedDocumentsAsync(session, entities.ToList(), referenceProperties);

        return entities
            .Select(e => entityMapper.ToPersistentObject(e, entityTypeDefinition.Id, includedDocuments))
            .DistinctBy(po => po.Id);
    }

    private EntityTypeDefinition? ResolveEntityTypeDefinition(SparkQuery query, string methodName)
    {
        // If EntityType is explicitly set, use it
        if (!string.IsNullOrEmpty(query.EntityType))
        {
            return modelLoader.GetEntityTypeByName(query.EntityType);
        }

        // Otherwise, we need to infer from the method return type — but we need the Actions class first.
        // For now, return null if not set (EntityType should be set for Custom queries).
        return null;
    }

    private static CustomQueryMethodInfo? ResolveCustomQueryMethod(Type actionsType, string methodName)
    {
        var cacheKey = $"{actionsType.FullName};{methodName}";
        return customQueryMethodCache.GetOrAdd(cacheKey, _ =>
        {
            var method = actionsType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method == null)
                return null;

            var returnType = method.ReturnType;
            var parameters = method.GetParameters();

            // Validate parameter: zero params or one CustomQueryArgs param
            bool acceptsArgs;
            if (parameters.Length == 0)
            {
                acceptsArgs = false;
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(CustomQueryArgs))
            {
                acceptsArgs = true;
            }
            else
            {
                return null; // Invalid signature
            }

            // Extract the element type from IQueryable<T> or IEnumerable<T>
            var elementType = ExtractQueryableElementType(returnType);
            if (elementType == null)
                return null;

            var isQueryable = typeof(IQueryable).IsAssignableFrom(returnType);

            return new CustomQueryMethodInfo
            {
                Method = method,
                AcceptsArgs = acceptsArgs,
                ResultElementType = elementType,
                IsQueryable = isQueryable,
            };
        });
    }

    private static Type? ExtractQueryableElementType(Type type)
    {
        // Check if the type itself is IQueryable<T>
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(IQueryable<>) || genericDef == typeof(IEnumerable<>))
                return type.GetGenericArguments()[0];
        }

        // Check implemented interfaces for IQueryable<T>
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQueryable<>))
                return iface.GetGenericArguments()[0];
        }

        // Check for IEnumerable<T> as fallback
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var elementType = iface.GetGenericArguments()[0];
                if (elementType != typeof(object))
                    return elementType;
            }
        }

        return null;
    }

    private static IEnumerable<object> MaterializeQueryable(object queryable, Type elementType)
    {
        // Call Queryable.ToList() on an in-memory IQueryable<T>
        var toListMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.ToList) && m.GetGenericArguments().Length == 1)
            .MakeGenericMethod(elementType);

        var result = toListMethod.Invoke(null, [queryable]);
        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToList();
        }
        return [];
    }

    private static Type? FindClrType(string clrTypeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetTypes()
                    .FirstOrDefault(t => (t.FullName == clrTypeName || t.Name == clrTypeName) && !t.IsAbstract && !t.IsInterface);
                if (type != null) return type;
            }
            catch (ReflectionTypeLoadException)
            {
                continue;
            }
        }
        return null;
    }

    #endregion

    #region Shared Helpers

    private object ApplyIndexByType(ISparkSession session, Type resultType, Type indexType)
    {
        // Find the Query<T>(Type) method on ISparkSession via reflection
        var sessionQueryMethod = typeof(ISparkSession).GetMethods()
            .FirstOrDefault(m => m.Name == "Query"
                && m.IsGenericMethod
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(Type));

        if (sessionQueryMethod == null)
        {
            throw new InvalidOperationException("Could not find Query<T>(Type) method on ISparkSession");
        }

        var genericMethod = sessionQueryMethod.MakeGenericMethod(resultType);
        return genericMethod.Invoke(session, [indexType])!;
    }

    private object ApplyIndexByName(ISparkSession session, Type entityType, string indexName)
    {
        // Find the Query<T>(string?) method on ISparkSession via reflection:
        // it has 1 generic type parameter and 1 string parameter.
        var sessionQueryMethod = typeof(ISparkSession).GetMethods()
            .FirstOrDefault(m => m.Name == "Query"
                && m.IsGenericMethod
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(string));

        if (sessionQueryMethod == null)
        {
            throw new InvalidOperationException("Could not find Query<T>(string?) method on ISparkSession");
        }

        var genericMethod = sessionQueryMethod.MakeGenericMethod(entityType);
        return genericMethod.Invoke(session, [indexName])!;
    }

    private object ApplySorting(object queryable, Type entityType, SortColumn[] sortColumns)
    {
        for (int i = 0; i < sortColumns.Length; i++)
        {
            var col = sortColumns[i];
            var propertyInfo = entityType.GetProperty(col.Property, BindingFlags.Public | BindingFlags.Instance);
            if (propertyInfo == null) continue;

            var isDescending = string.Equals(col.Direction, "desc", StringComparison.OrdinalIgnoreCase);
            var methodName = i == 0
                ? (isDescending ? "OrderByDescending" : "OrderBy")
                : (isDescending ? "ThenByDescending" : "ThenBy");

            var parameter = System.Linq.Expressions.Expression.Parameter(entityType, "x");
            var propertyAccess = System.Linq.Expressions.Expression.Property(parameter, propertyInfo);
            var lambda = System.Linq.Expressions.Expression.Lambda(propertyAccess, parameter);

            var orderMethod = typeof(Queryable).GetMethods()
                .First(m => m.Name == methodName && m.GetParameters().Length == 2)
                .MakeGenericMethod(entityType, propertyInfo.PropertyType);

            queryable = orderMethod.Invoke(null, [queryable, lambda])!;
        }
        return queryable;
    }

    private async Task<IEnumerable<object>> ExecuteQueryableAsync(object queryable, Type entityType)
    {
        // Call storageProvider.ToListAsync<T>(queryable, CancellationToken) via reflection
        var toListMethod = typeof(ISparkStorageProvider).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(ISparkStorageProvider.ToListAsync)
                && m.IsGenericMethod
                && m.GetGenericArguments().Length == 1
                && m.GetParameters().Length == 2);

        if (toListMethod == null)
        {
            return [];
        }

        var genericToListMethod = toListMethod.MakeGenericMethod(entityType);
        var task = genericToListMethod.Invoke(storageProvider, [queryable, CancellationToken.None]) as Task;

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

    #endregion
}

internal sealed class CustomQueryMethodInfo
{
    public required MethodInfo Method { get; init; }
    public required bool AcceptsArgs { get; init; }
    public required Type ResultElementType { get; init; }
    public required bool IsQueryable { get; init; }
}
