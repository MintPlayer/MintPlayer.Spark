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
    [Inject] private readonly Services.Breadcrumb.IBreadcrumbResolver breadcrumbResolver;
    [Inject] private readonly Services.IRowSecurity rowSecurity;

    /// <summary>How often (in batches) a live stream re-checks its type-level authorization.</summary>
    private const int ReauthorizeEveryNBatches = 10;

    public async IAsyncEnumerable<StreamingQueryBatch> ExecuteStreamingQueryAsync(
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

        // Resolved once: a stream is one result whose rows arrive over time, so its shape is fixed
        // when it opens.
        var columns = Services.QueryResultProjector.BuildColumns(entityTypeDef);

        // Resolve CLR type and Actions class. Both failures are refused at --spark-verify-model and
        // at query-load time (SparkComposedQueries); reaching either here means the model changed
        // under a running process, so the message still has to stand on its own inside a websocket
        // frame that the client renders as one line.
        if (SparkComposedQueries.IsComposed(entityTypeDef))
        {
            throw new InvalidOperationException(
                $"Streaming query '{query.Name}' streams over '{entityTypeDef.Name}', which declares no " +
                $"clrType. Streaming watches a RavenDB collection for changes, and a composed type has no " +
                $"collection to watch — its rows are computed by {entityTypeDef.Name}Actions. Serve it as a " +
                $"non-streaming query instead.");
        }

        var entityType = SparkTypeResolver.ResolveClrType(entityTypeDef.ClrType)
            ?? throw new InvalidOperationException(
                $"Streaming query '{query.Name}' maps rows to '{entityTypeDef.Name}', whose clrType " +
                $"'{entityTypeDef.ClrType}' is not declared by any loaded assembly. Re-run " +
                $"'--spark-synchronize-model' if the class was renamed, or reference the assembly declaring it.");

        var actionsInstance = actionsResolver.ResolveForType(entityType);

        // Find streaming method
        var methodInfo = ResolveStreamingMethod(actionsInstance.GetType(), methodName);
        if (methodInfo is null)
        {
            throw new InvalidOperationException(
                $"Streaming method '{methodName}' not found on '{actionsInstance.GetType().Name}'. " +
                $"Expected a method returning IAsyncEnumerable<IReadOnlyList<T>> with parameters (StreamingQueryArgs, CancellationToken).");
        }

        // Open the connection session and invoke the streaming method. This session is the
        // consumer's, handed to StreamItems via args.Session and held for the whole enumeration.
        // #239 M4: a socket open for minutes is not "a request" — its request count would sail past
        // RavenDB's 30-cap (a per-request N+1 alarm) purely from doing its job — so uncap it. The
        // framework's own per-batch work runs on a fresh session inside the loop and STAYS at 30,
        // so the N+1 alarm still guards the framework paths.
        using var session = documentStore.OpenAsyncSession();
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
        var args = new StreamingQueryArgs
        {
            Query = query,
            Session = session,
            CancellationToken = cancellationToken,
        };

        var result = methodInfo.Method.Invoke(actionsInstance, [args, cancellationToken]);
        if (result is null) yield break;

        // Iterate via IAsyncEnumerable reflection
        var batchesSinceReauth = 0;
        await foreach (var batch in IterateAsyncEnumerable(result, methodInfo.ElementType, methodInfo.IsSingleItemStream, cancellationToken))
        {
            // Security sweep L1: the type-level authorization ran once, before the loop. A stream
            // outlives that snapshot — so re-run it every few batches, and close the stream if the
            // right was revoked (e.g. the group's Query right removed from security.json). This
            // bounds how long a stream keeps delivering after its authorization should have ended.
            // (Residual: user-level revocation carried in the frozen handshake ClaimsPrincipal —
            // e.g. the user removed from a group — needs a credential refresh that is
            // auth-scheme-specific; tracked separately.)
            if (++batchesSinceReauth >= ReauthorizeEveryNBatches)
            {
                batchesSinceReauth = 0;
                if (!await permissionService.IsAllowedAsync("Query", entityTypeDef.Name))
                    yield break;

                // #239 M3: drop the per-request row-filter memo on the same tick. RowSecurity is
                // scoped to this connection, so without this the row filter would be frozen at
                // connect for the socket's whole life — a caller whose allow-list shrinks would keep
                // receiving revoked rows. Clearing here bounds row-filter staleness to this interval,
                // matching the type-level re-check above.
                rowSecurity.ResetRequestFilterCache();
            }

            // #239 M4: the framework's own per-batch reads (row-filter projection reload, breadcrumb
            // BFS, redaction reload) run on a FRESH session per batch — not the connection session —
            // so their request count starts from zero each batch and the identity map is bounded to
            // one batch (fixing an unbounded-memory leak on long streams too). Reusing one session
            // across all batches is the pre-existing bug that made a referenced-type stream die
            // around batch ~8-15.
            using var batchSession = documentStore.OpenAsyncSession();

            // Row-level authorization, per batch. A stream is a long-lived subscription that
            // keeps delivering rows, so skipping the check here would not merely disclose the
            // rows present when it opened — it would keep disclosing every new one for as long
            // as the client stays connected.
            var batchList = await rowSecurity.FilterAsync(
                batchSession,
                batch as IReadOnlyList<object> ?? batch.ToList(),
                entityType,
                methodInfo.ElementType,
                "Query");

            if (batchList.Count == 0) continue;

            // Resolve breadcrumbs (recursive, batched) for this batch.
            var breadcrumbs = await breadcrumbResolver.ResolveAsync(batchSession, batchList, entityTypeDef, cancellationToken);

            var mapped = batchList
                .Select(e => (Po: entityMapper.ToPersistentObject(e, entityTypeDef.Id, breadcrumbs), Row: e))
                .ToList();
            await rowSecurity.RedactAsync(batchSession, mapped, entityType, methodInfo.ElementType, "Query");

            // Same projection as a paged query, so a streamed row and a fetched row are the same
            // shape on the wire — including the refusal of a row with no id, which on a stream would
            // otherwise be dropped silently by the diff engine (it keys state on the id).
            yield return new StreamingQueryBatch(
                columns,
                Services.QueryResultProjector.ToItems(mapped.Select(m => m.Po), columns, query.Name));
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

}

internal sealed class StreamingMethodInfo
{
    public required MethodInfo Method { get; init; }
    public required Type ElementType { get; init; }
    public required Type BatchType { get; init; }
    public required bool IsSingleItemStream { get; init; }
}
