using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using System.Reflection;

namespace MintPlayer.Spark.Services;

public interface IQueryExecutor
{
    Task<QueryResult> ExecuteQueryAsync(SparkQuery query, PersistentObject? parent = null, int skip = 0, int take = 50, string? search = null);
}

[Register(typeof(IQueryExecutor), ServiceLifetime.Scoped)]
internal partial class QueryExecutor : IQueryExecutor
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkContextResolver sparkContextResolver;
    [Inject] private readonly IIndexRegistry indexRegistry;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IReferenceResolver referenceResolver;
    [Inject] private readonly Breadcrumb.IBreadcrumbResolver breadcrumbResolver;

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
        var sparkContext = sparkContextResolver.ResolveContext(session);
        if (sparkContext == null)
        {
            return [];
        }

        var contextType = sparkContext.GetType();
        var property = contextType.GetCachedProperty(propertyName);

        if (property == null || !property.CanRead)
        {
            return [];
        }

        var queryable = AccessorCache.GetGetter(property)(sparkContext);
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

        Type? indexType = registration?.IndexType;
        if (!string.IsNullOrEmpty(indexName) && indexType != null)
        {
            queryable = ApplyIndexWithType(session, entityType, indexType);
        }
        else if (!string.IsNullOrEmpty(indexName))
        {
            queryable = ApplyIndexByName(session, entityType, indexName);
        }

        if (!string.IsNullOrEmpty(indexName) && resultType != entityType)
        {
            queryable = ApplyProjection(queryable, resultType);
        }

        // Resolve reference properties before executing so we can chain .Include()
        var referenceProperties = referenceResolver.GetReferenceProperties(resultType, entityType);
        if (referenceProperties.Count > 0)
        {
            queryable = referenceResolver.ApplyIncludes(queryable, referenceProperties);
        }

        var sortType = (indexType != null && resultType != entityType) ? resultType : entityType;
        if (query.SortColumns.Length > 0)
        {
            queryable = ApplySorting(queryable, sortType, query.SortColumns);
        }

        var entities = (await ExecuteQueryableAsync(queryable, resultType)).ToList();

        // Referenced docs were primed into the session cache by .Include() above; the resolver's
        // first batched load is a cache hit, deeper breadcrumb levels cost one request each.
        var breadcrumbs = await breadcrumbResolver.ResolveAsync(session, entities, entityTypeDefinition);

        return entities
            .Select(e => entityMapper.ToPersistentObject(e, entityTypeDefinition.Id, breadcrumbs))
            .DistinctBy(po => po.Id);
    }

    #endregion

    #region Custom Queries

    private async Task<IEnumerable<PersistentObject>> ExecuteCustomQueryAsync(
        SparkQuery query, string methodName, PersistentObject? parent)
    {
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

        // Await async methods (Task<IEnumerable<T>>, Task<IQueryable<T>>, etc.)
        if (methodInfo.IsAsync && result is Task task)
        {
            await task;
            result = task.GetCompletedTaskResult();
        }

        if (result == null)
        {
            return [];
        }

        // Apply index projection for computed/stored fields (e.g., FullName from People_Overview).
        // Without this, RavenDB loads full documents which lack computed index fields.
        if (methodInfo.IsRavenQueryable && indexRegistry.IsProjectionType(methodInfo.ResultElementType))
        {
            result = ApplyProjection(result, methodInfo.ResultElementType);
        }

        // Apply sorting if the result is IQueryable
        if (methodInfo.IsQueryable && query.SortColumns.Length > 0)
        {
            result = ApplySorting(result, methodInfo.ResultElementType, query.SortColumns);
        }

        // Materialize results
        IEnumerable<object> entities;
        if (methodInfo.IsRavenQueryable)
        {
            entities = await ExecuteQueryableAsync(result, methodInfo.ResultElementType);
        }
        else if (result is IQueryable)
        {
            // In-memory IQueryable — materialize via LINQ ToList
            entities = MaterializeQueryable(result, methodInfo.ResultElementType);
        }
        else if (result is System.Collections.IEnumerable enumerable)
        {
            entities = enumerable.Cast<object>().ToList();
        }
        else
        {
            return [];
        }

        // Resolve breadcrumbs (recursive, batched) for the custom query's results.
        var entityList = entities as IReadOnlyList<object> ?? entities.ToList();
        var breadcrumbs = await breadcrumbResolver.ResolveAsync(session, entityList, entityTypeDefinition);

        return entityList
            .Select(e => entityMapper.ToPersistentObject(e, entityTypeDefinition.Id, breadcrumbs))
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

    /// <summary>
    /// Resolves the custom query method info from the given actions type and method name, with caching for performance.
    /// </summary>
    /// <param name="actionsType"></param>
    /// <param name="methodName"></param>
    /// <returns></returns>
    private static CustomQueryMethodInfo? ResolveCustomQueryMethod(Type actionsType, string methodName)
    {
        return ReflectionCache.GetOrAdd<(string Op, Type Type, string Method), CustomQueryMethodInfo?>(
            ("QueryExecutor.CustomQueryMethod", actionsType, methodName),
            static k =>
        {
            var method = k.Type.GetMethod(k.Method, BindingFlags.Public | BindingFlags.Instance);
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

            // Unwrap Task<T> for async methods
            var isAsync = false;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                isAsync = true;
                returnType = returnType.GetGenericArguments()[0];
            }

            // Extract the element type from IQueryable<T> or IRavenQueryable<T>
            var elementType = ExtractQueryableElementType(returnType);
            if (elementType == null)
                return null;

            var isRavenQueryable = !isAsync && typeof(IRavenQueryable<>).MakeGenericType(elementType).IsAssignableFrom(returnType);
            var isQueryable = !isAsync && typeof(IQueryable).IsAssignableFrom(returnType);

            return new CustomQueryMethodInfo
            {
                Method = method,
                AcceptsArgs = acceptsArgs,
                ResultElementType = elementType,
                IsQueryable = isQueryable,
                IsRavenQueryable = isRavenQueryable,
                IsAsync = isAsync,
            };
        });
    }

    private static Type? ExtractQueryableElementType(Type type)
    {
        return ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("QueryExecutor.QueryableElement", type),
            static k =>
            {
                var t = k.Type;
                // Check if the type itself is IQueryable<T>
                if (t.IsGenericType)
                {
                    var genericDef = t.GetGenericTypeDefinition();
                    if (genericDef == typeof(IQueryable<>) || genericDef == typeof(IEnumerable<>))
                        return t.GetGenericArguments()[0];
                }

                // Check implemented interfaces for IQueryable<T>
                foreach (var iface in t.GetInterfaces())
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQueryable<>))
                        return iface.GetGenericArguments()[0];
                }

                // Check for IEnumerable<T> as fallback
                foreach (var iface in t.GetInterfaces())
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    {
                        var elementType = iface.GetGenericArguments()[0];
                        if (elementType != typeof(object))
                            return elementType;
                    }
                }

                return null;
            });
    }

    private static IEnumerable<object> MaterializeQueryable(object queryable, Type elementType)
    {
        // Call Queryable.ToList() on an in-memory IQueryable<T>
        var toListMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo>(
            ("QueryExecutor.EnumerableToList", elementType),
            static k => typeof(Enumerable).GetMethods()
                .First(m => m.Name == nameof(Enumerable.ToList) && m.GetGenericArguments().Length == 1)
                .MakeGenericMethod(k.Type));

        var result = toListMethod.Invoke(null, [queryable]);
        if (result is System.Collections.IEnumerable enumerable)
        {
            return enumerable.Cast<object>().ToList();
        }
        return [];
    }

    private static Type? FindClrType(string clrTypeName)
    {
        return ReflectionCache.GetOrAdd<Type?>(
            $"clrType|{clrTypeName}",
            () =>
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
            });
    }

    #endregion

    #region Shared Helpers

    /// <summary>
    /// Returns session.Query&lt;resultType, indexType&gt;()
    /// </summary>
    /// <param name="session">The asynchronous document session to execute the query on.</param>
    /// <param name="resultType">The type of the query result.</param>
    /// <param name="indexType">The type of the index to use for the query.</param>
    /// <returns>The result of the invoked generic query.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the required generic Query&lt;T, TIndexCreator&gt; method cannot be found on the session.</exception>
    private object ApplyIndexWithType(IAsyncDocumentSession session, Type resultType, Type indexType)
    {
        var genericMethod = ReflectionCache.GetOrAdd<(string Op, Type Result, Type Index), MethodInfo>(
            ("QueryExecutor.SessionQueryByIndexCreator", resultType, indexType),
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
        return genericMethod.Invoke(session, [])!;
    }

    /// <summary>
    /// Returns session.Query&lt;entityType&gt;(indexName, null, false)
    /// </summary>
    /// <param name="session"></param>
    /// <param name="entityType"></param>
    /// <param name="indexName"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    private object ApplyIndexByName(IAsyncDocumentSession session, Type entityType, string indexName)
    {
        var genericMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo>(
            ("QueryExecutor.SessionQueryByIndexName", entityType),
            static k =>
            {
                var sessionQueryMethod = typeof(IAsyncDocumentSession).GetMethods()
                    .FirstOrDefault(m => m.Name == "Query"
                        && m.IsGenericMethod
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 3
                        && m.GetParameters()[0].ParameterType == typeof(string)
                        && m.GetParameters()[1].ParameterType == typeof(string)
                        && m.GetParameters()[2].ParameterType == typeof(bool))
                    ?? throw new InvalidOperationException("Could not find Query<T>(string, string, bool) method on IAsyncDocumentSession");
                return sessionQueryMethod.MakeGenericMethod(k.Type);
            });
        var ravenIndexName = indexName.Replace("_", "/");
        return genericMethod.Invoke(session, [ravenIndexName, null, false])!;
    }

    /// <summary>
    /// Returns queryable.ProjectInto&lt;resultType&gt;() to apply index projections for computed/stored fields.
    /// </summary>
    /// <param name="queryable"></param>
    /// <param name="resultType"></param>
    /// <returns></returns>
    private object ApplyProjection(object queryable, Type resultType)
    {
        var genericProjectMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("QueryExecutor.LinqProjectInto", resultType),
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

        if (genericProjectMethod == null)
        {
            return queryable;
        }

        return genericProjectMethod.Invoke(null, [queryable])!;
    }

    /// <summary>
    /// Applies sorting to the queryable based on the provided sort columns.
    /// </summary>
    /// <param name="queryable"></param>
    /// <param name="entityType"></param>
    /// <param name="sortColumns"></param>
    /// <returns></returns>
    private object ApplySorting(object queryable, Type entityType, SortColumn[] sortColumns)
    {
        for (int i = 0; i < sortColumns.Length; i++)
        {
            var col = sortColumns[i];
            var propertyInfo = entityType.GetCachedProperty(col.Property);
            if (propertyInfo == null) continue;

            var isDescending = string.Equals(col.Direction, "desc", StringComparison.OrdinalIgnoreCase);
            var methodName = i == 0
                ? (isDescending ? "OrderByDescending" : "OrderBy")
                : (isDescending ? "ThenByDescending" : "ThenBy");

            var parameter = System.Linq.Expressions.Expression.Parameter(entityType, "x");
            var propertyAccess = System.Linq.Expressions.Expression.Property(parameter, propertyInfo);
            var lambda = System.Linq.Expressions.Expression.Lambda(propertyAccess, parameter);

            var orderMethod = ReflectionCache.GetOrAdd<(string Op, string Method, Type Entity, Type Prop), MethodInfo>(
                ("QueryExecutor.QueryableOrder", methodName, entityType, propertyInfo.PropertyType),
                static k => typeof(Queryable).GetMethods()
                    .First(m => m.Name == k.Method && m.GetParameters().Length == 2)
                    .MakeGenericMethod(k.Entity, k.Prop));

            queryable = orderMethod.Invoke(null, [queryable, lambda])!;
        }
        return queryable;
    }

    /// <summary>
    /// Materializes an IRavenQueryable<T> by calling ToListAsync via reflection.
    /// </summary>
    /// <param name="queryable"></param>
    /// <param name="entityType"></param>
    /// <returns></returns>
    private async Task<IEnumerable<object>> ExecuteQueryableAsync(object queryable, Type entityType)
    {
        var genericToListMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("QueryExecutor.LinqToListAsync", entityType),
            static k =>
            {
                var toListMethod = typeof(LinqExtensions).GetMethods()
                    .FirstOrDefault(m => m.Name == nameof(LinqExtensions.ToListAsync)
                        && m.GetGenericArguments().Length == 1
                        && m.GetParameters().Length == 2);
                return toListMethod?.MakeGenericMethod(k.Type);
            });

        if (genericToListMethod == null)
        {
            return [];
        }

        var task = genericToListMethod.Invoke(null, [queryable, CancellationToken.None]) as Task;

        if (task == null)
        {
            return [];
        }

        await task;

        var result = task.GetCompletedTaskResult();

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
    public required bool IsRavenQueryable { get; init; }
    public required bool IsAsync { get; init; }
}
