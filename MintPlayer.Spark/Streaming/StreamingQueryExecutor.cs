using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Streaming;

[Register(typeof(IStreamingQueryExecutor), ServiceLifetime.Scoped)]
internal partial class StreamingQueryExecutor : IStreamingQueryExecutor
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IActionsResolver actionsResolver;

    private static readonly ConcurrentDictionary<string, StreamingMethodInfo?> streamingMethodCache = new();

    public async IAsyncEnumerable<PersistentObject[]> ExecuteStreamingQueryAsync(
        SparkQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Validate source
        if (!query.Source.StartsWith("Custom.", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Streaming query '{query.Name}' must use a Custom.* source, got '{query.Source}'.");
        }

        var methodName = query.Source[7..];

        // Resolve entity type definition
        if (string.IsNullOrEmpty(query.EntityType))
        {
            throw new InvalidOperationException(
                $"Streaming query '{query.Name}' requires EntityType to be set.");
        }

        var entityTypeDef = modelLoader.GetEntityTypeByName(query.EntityType);
        if (entityTypeDef is null)
        {
            throw new InvalidOperationException(
                $"Entity type '{query.EntityType}' not found for streaming query '{query.Name}'.");
        }

        // Check authorization
        await permissionService.EnsureAuthorizedAsync("Query", entityTypeDef.Name);

        // Resolve CLR type and Actions class
        var entityType = FindClrType(entityTypeDef.ClrType);
        if (entityType is null)
        {
            throw new InvalidOperationException(
                $"CLR type '{entityTypeDef.ClrType}' not found for streaming query '{query.Name}'.");
        }

        var actionsInstance = actionsResolver.ResolveForType(entityType);

        // Find streaming method
        var methodInfo = ResolveStreamingMethod(actionsInstance.GetType(), methodName);
        if (methodInfo is null)
        {
            throw new InvalidOperationException(
                $"Streaming method '{methodName}' not found on '{actionsInstance.GetType().Name}'. " +
                $"Expected a method returning IAsyncEnumerable<IReadOnlyList<T>> with parameters (StreamingQueryArgs, CancellationToken).");
        }

        // Open a session and invoke the streaming method
        using var session = documentStore.OpenAsyncSession();
        var args = new StreamingQueryArgs
        {
            Query = query,
            Session = session,
            CancellationToken = cancellationToken,
        };

        var result = methodInfo.Method.Invoke(actionsInstance, [args, cancellationToken]);
        if (result is null) yield break;

        // Iterate via IAsyncEnumerable reflection
        await foreach (var batch in IterateAsyncEnumerable(result, methodInfo.ElementType, cancellationToken))
        {
            // Map each entity in the batch to PersistentObject
            var persistentObjects = batch
                .Select(e => entityMapper.ToPersistentObject(e, entityTypeDef.Id))
                .ToArray();
            yield return persistentObjects;
        }
    }

    private static StreamingMethodInfo? ResolveStreamingMethod(Type actionsType, string methodName)
    {
        var cacheKey = $"stream;{actionsType.FullName};{methodName}";
        return streamingMethodCache.GetOrAdd(cacheKey, _ =>
        {
            var method = actionsType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method is null) return null;

            var returnType = method.ReturnType;
            var parameters = method.GetParameters();

            // Validate parameters: (StreamingQueryArgs, CancellationToken)
            if (parameters.Length != 2 ||
                parameters[0].ParameterType != typeof(StreamingQueryArgs) ||
                parameters[1].ParameterType != typeof(CancellationToken))
            {
                return null;
            }

            // Validate return type: IAsyncEnumerable<IReadOnlyList<T>>
            var asyncEnumerableType = ExtractAsyncEnumerableType(returnType);
            if (asyncEnumerableType is null) return null;

            // asyncEnumerableType should be IReadOnlyList<T>
            var elementType = ExtractReadOnlyListElementType(asyncEnumerableType);
            if (elementType is null) return null;

            return new StreamingMethodInfo
            {
                Method = method,
                ElementType = elementType,
                BatchType = asyncEnumerableType,
            };
        });
    }

    private static Type? ExtractAsyncEnumerableType(Type type)
    {
        // Check if type implements IAsyncEnumerable<T>
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    private static Type? ExtractReadOnlyListElementType(Type type)
    {
        // Check IReadOnlyList<T>
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    private static async IAsyncEnumerable<IReadOnlyList<object>> IterateAsyncEnumerable(
        object asyncEnumerable, Type elementType, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Get IAsyncEnumerable<IReadOnlyList<T>>.GetAsyncEnumerator()
        var batchType = typeof(IReadOnlyList<>).MakeGenericType(elementType);
        var asyncEnumerableType = typeof(IAsyncEnumerable<>).MakeGenericType(batchType);

        var getEnumeratorMethod = asyncEnumerableType.GetMethod("GetAsyncEnumerator")!;
        var enumerator = getEnumeratorMethod.Invoke(asyncEnumerable, [cancellationToken])!;

        // Use the interface type to resolve methods — compiler-generated async enumerators
        // use explicit interface implementation, so MoveNextAsync/Current won't be found
        // on the concrete type.
        var enumeratorInterfaceType = typeof(IAsyncEnumerator<>).MakeGenericType(batchType);
        var moveNextMethod = enumeratorInterfaceType.GetMethod("MoveNextAsync")!;
        var currentProperty = enumeratorInterfaceType.GetProperty("Current")!;

        try
        {
            while (true)
            {
                var moveNextResult = moveNextMethod.Invoke(enumerator, []);
                bool hasMore;
                if (moveNextResult is ValueTask<bool> valueTask)
                {
                    hasMore = await valueTask;
                }
                else
                {
                    throw new InvalidOperationException("Unexpected MoveNextAsync return type");
                }

                if (!hasMore) break;

                var currentBatch = currentProperty.GetValue(enumerator);
                if (currentBatch is System.Collections.IEnumerable enumerable)
                {
                    yield return enumerable.Cast<object>().ToList();
                }
            }
        }
        finally
        {
            // Dispose the enumerator
            if (enumerator is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }
    }

    private static Type? FindClrType(string clrTypeName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetTypes()
                    .FirstOrDefault(t => (t.FullName == clrTypeName || t.Name == clrTypeName) && !t.IsAbstract && !t.IsInterface);
                if (type is not null) return type;
            }
            catch (ReflectionTypeLoadException)
            {
                continue;
            }
        }
        return null;
    }
}

internal sealed class StreamingMethodInfo
{
    public required MethodInfo Method { get; init; }
    public required Type ElementType { get; init; }
    public required Type BatchType { get; init; }
}
