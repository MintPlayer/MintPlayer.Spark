using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using System.Reflection;

namespace MintPlayer.Spark.RavenDB;

/// <summary>
/// RavenDB implementation of <see cref="ISparkStorageProvider"/>.
/// Handles index creation, query materialization, includes, and projections.
/// </summary>
public class RavenDbStorageProvider : ISparkStorageProvider
{
    private readonly IDocumentStore _documentStore;

    public RavenDbStorageProvider(IDocumentStore documentStore)
    {
        _documentStore = documentStore;
    }

    /// <inheritdoc />
    public void Initialize(IApplicationBuilder app)
    {
        var indexRegistry = app.ApplicationServices.GetRequiredService<IIndexRegistry>();
        var targetAssembly = Assembly.GetEntryAssembly();

        if (targetAssembly == null)
        {
            Console.WriteLine("Warning: Could not determine entry assembly for index creation.");
            return;
        }

        try
        {
            // Find and register all index types (AbstractIndexCreationTask<T>)
            var indexTypes = targetAssembly.GetTypes()
                .Where(t => !t.IsAbstract && IsAbstractIndexCreationTask(t))
                .ToList();

            foreach (var indexType in indexTypes)
            {
                var collectionType = GetCollectionTypeFromIndex(indexType);
                if (collectionType == null)
                {
                    Console.WriteLine($"Warning: Could not determine collection type for index {indexType.Name}");
                    continue;
                }

                var indexName = indexType.Name;
                indexRegistry.RegisterIndex(indexName, collectionType, indexType);
            }

            // Find and register all projection types with [FromIndex] attribute
            var projectionTypes = targetAssembly.GetTypes()
                .Where(t => t.GetCustomAttribute<FromIndexAttribute>() != null)
                .ToList();

            foreach (var projectionType in projectionTypes)
            {
                var attr = projectionType.GetCustomAttribute<FromIndexAttribute>()!;
                indexRegistry.RegisterProjection(projectionType, attr.IndexType);
            }

            // Create indexes in RavenDB
            IndexCreation.CreateIndexes(targetAssembly, _documentStore);
            Console.WriteLine($"RavenDB indexes created/updated from assembly: {targetAssembly.GetName().Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating RavenDB indexes: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken ct = default)
    {
        // RavenDB's IRavenQueryable needs LinqExtensions.ToListAsync for async materialization
        return await LinqExtensions.ToListAsync(queryable, ct);
    }

    /// <inheritdoc />
    public object ApplyIncludes(object queryable, IEnumerable<string> propertyPaths)
    {
        // RavenDB's .Include() is applied via reflection since the queryable type is generic
        var result = queryable;
        foreach (var path in propertyPaths)
        {
            var includeMethod = result.GetType().GetMethod("Include", [typeof(string)]);
            if (includeMethod != null)
            {
                result = includeMethod.Invoke(result, [path])!;
            }
        }
        return result;
    }

    /// <inheritdoc />
    public object ApplyProjection(object queryable, Type resultType)
    {
        // Call ProjectInto<TResult>() via reflection
        var projectIntoMethod = typeof(LinqExtensions)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "ProjectInto" && m.GetGenericArguments().Length == 1);

        if (projectIntoMethod != null)
        {
            var genericMethod = projectIntoMethod.MakeGenericMethod(resultType);
            return genericMethod.Invoke(null, [queryable])!;
        }

        return queryable;
    }

    /// <inheritdoc />
    public string? GetCollectionName(Type clrType)
    {
        return _documentStore.Conventions.GetCollectionName(clrType);
    }

    private static bool IsAbstractIndexCreationTask(Type type)
    {
        var current = type;
        while (current != null && current != typeof(object))
        {
            if (current.IsGenericType)
            {
                var genericDef = current.GetGenericTypeDefinition();
                if (genericDef == typeof(AbstractIndexCreationTask<>) ||
                    genericDef == typeof(AbstractMultiMapIndexCreationTask<>))
                {
                    return true;
                }
            }
            current = current.BaseType;
        }
        return false;
    }

    private static Type? GetCollectionTypeFromIndex(Type indexType)
    {
        var current = indexType;
        while (current != null && current != typeof(object))
        {
            if (current.IsGenericType)
            {
                var genericDef = current.GetGenericTypeDefinition();
                if (genericDef == typeof(AbstractIndexCreationTask<>) ||
                    genericDef == typeof(AbstractMultiMapIndexCreationTask<>))
                {
                    return current.GetGenericArguments()[0];
                }
            }
            current = current.BaseType;
        }
        return null;
    }
}
