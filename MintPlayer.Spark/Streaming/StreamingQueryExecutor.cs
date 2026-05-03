using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MintPlayer.Spark.Streaming;

[Register(typeof(IStreamingQueryExecutor), ServiceLifetime.Scoped)]
internal partial class StreamingQueryExecutor : IStreamingQueryExecutor
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IReferenceResolver referenceResolver;

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

        // Get reference properties once for the entity type
        var referenceProperties = referenceResolver.GetReferenceProperties(methodInfo.ElementType);

        // Iterate via IAsyncEnumerable reflection
        await foreach (var batch in IterateAsyncEnumerable(result, methodInfo.ElementType, methodInfo.IsSingleItemStream, cancellationToken))
        {
            // Resolve reference breadcrumbs for this batch
            var includedDocuments = await referenceResolver.ResolveReferencedDocumentsAsync(session, batch, referenceProperties);

            // Map each entity in the batch to PersistentObject
            var persistentObjects = batch
                .Select(e => entityMapper.ToPersistentObject(e, entityTypeDef.Id, includedDocuments))
                .ToArray();
            yield return persistentObjects;
        }
    }

    private static StreamingMethodInfo? ResolveStreamingMethod(Type actionsType, string methodName)
    {
        return ReflectionCache.GetOrAdd<(string Op, Type Type, string Method), StreamingMethodInfo?>(
            ("StreamingQueryExecutor.ResolveStreamingMethod", actionsType, methodName),
            static k =>
        {
            var method = k.Type.GetMethod(k.Method, BindingFlags.Public | BindingFlags.Instance);
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

            // Validate return type: IAsyncEnumerable<IReadOnlyList<T>> or IAsyncEnumerable<T>
            var asyncEnumerableType = ExtractAsyncEnumerableType(returnType);
            if (asyncEnumerableType is null) return null;

            // Check if it's IAsyncEnumerable<IReadOnlyList<T>> (batch) or IAsyncEnumerable<T> (single)
            var batchElementType = ExtractReadOnlyListElementType(asyncEnumerableType);
            if (batchElementType is not null)
            {
                return new StreamingMethodInfo
                {
                    Method = method,
                    ElementType = batchElementType,
                    BatchType = asyncEnumerableType,
                    IsSingleItemStream = false,
                };
            }

            // Single-item stream: IAsyncEnumerable<T>
            return new StreamingMethodInfo
            {
                Method = method,
                ElementType = asyncEnumerableType,
                BatchType = asyncEnumerableType,
                IsSingleItemStream = true,
            };
        });
    }

    private static Type? ExtractAsyncEnumerableType(Type type)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("StreamingQueryExecutor.AsyncEnumerableElement", type),
            static k =>
            {
                var t = k.Type;
                // Check if type implements IAsyncEnumerable<T>
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                    return t.GetGenericArguments()[0];

                foreach (var iface in t.GetInterfaces())
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
                        return iface.GetGenericArguments()[0];
                }

                return null;
            });

    private static Type? ExtractReadOnlyListElementType(Type type)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), Type?>(
            ("StreamingQueryExecutor.ReadOnlyListElement", type),
            static k =>
            {
                var t = k.Type;
                // Check IReadOnlyList<T>
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                    return t.GetGenericArguments()[0];

                foreach (var iface in t.GetInterfaces())
                {
                    if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                        return iface.GetGenericArguments()[0];
                }

                return null;
            });

    private static async IAsyncEnumerable<IReadOnlyList<object>> IterateAsyncEnumerable(
        object asyncEnumerable, Type elementType, bool isSingleItem, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // For batch streams: IAsyncEnumerable<IReadOnlyList<T>>
        // For single-item streams: IAsyncEnumerable<T>
        // Cache the closed IAsyncEnumerator<T> + its MoveNextAsync/Current MemberInfos per
        // (elementType, isSingleItem) pair — they're stable for the AppDomain.
        var (getEnumeratorMethod, moveNextMethod, currentProperty) = ReflectionCache.GetOrAdd<(string Op, Type Element, bool Single), (MethodInfo, MethodInfo, PropertyInfo)>(
            ("StreamingQueryExecutor.AsyncEnumeratorOps", elementType, isSingleItem),
            static k =>
            {
                var innerType = k.Single ? k.Element : typeof(IReadOnlyList<>).MakeGenericType(k.Element);
                var enumerableType = typeof(IAsyncEnumerable<>).MakeGenericType(innerType);
                var enumeratorInterface = typeof(IAsyncEnumerator<>).MakeGenericType(innerType);
                return (
                    enumerableType.GetMethod("GetAsyncEnumerator")!,
                    enumeratorInterface.GetMethod("MoveNextAsync")!,
                    enumeratorInterface.GetProperty("Current")!);
            });

        var enumerator = getEnumeratorMethod.Invoke(asyncEnumerable, [cancellationToken])!;
        var currentGetter = AccessorCache.GetGetter(currentProperty);

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

                var current = currentGetter(enumerator);
                if (isSingleItem)
                {
                    // Wrap single item in a list
                    if (current is not null)
                        yield return [current];
                }
                else if (current is System.Collections.IEnumerable enumerable)
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
                        if (type is not null) return type;
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        continue;
                    }
                }
                return null;
            });
    }
}

internal sealed class StreamingMethodInfo
{
    public required MethodInfo Method { get; init; }
    public required Type ElementType { get; init; }
    public required Type BatchType { get; init; }
    public required bool IsSingleItemStream { get; init; }
}
