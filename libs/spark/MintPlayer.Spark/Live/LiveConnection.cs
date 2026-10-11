using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Live;

/// <summary>
/// One <c>/spark/live</c> socket: a receive loop for <c>subscribe</c> / <c>update</c> / <c>unsubscribe</c>, and a
/// single worker that evaluates dirty subscriptions one at a time, each in a fresh DI scope under the socket's
/// principal, and sends what changed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded by construction.</b> A subscription is a dirty bit, never a queue: any number of changes between
/// two evaluations cost one evaluation (the latest state wins), at most one per subscription per
/// <see cref="SparkLiveOptions.Throttle"/>, and the worker sends inline, so a slow client slows its own
/// evaluations instead of growing a buffer. A frame not taken within <see cref="SparkLiveOptions.SendTimeout"/>
/// aborts the socket. Memory per socket is the last wire state of each subscription.
/// </para>
/// <para>
/// <b>One worker, sequential.</b> Every evaluation shares the socket's <see cref="HttpContext"/> (the principal,
/// the presentation token of the boundary net), which is not thread-safe; evaluating one subscription at a
/// time keeps it single-threaded, and bounds the CPU one socket can take.
/// </para>
/// </remarks>
internal sealed class LiveConnection
{
    private readonly WebSocket socket;
    private readonly LiveHub hub;
    private readonly SparkLiveOptions options;
    private readonly IQueryLoader queryLoader;
    private readonly IModelLoader modelLoader;
    private readonly IIndexCatalog indexCatalog;
    private readonly IDocumentStore store;
    private readonly TimeProvider time;
    private readonly DateTimeOffset deadline;
    private readonly Dictionary<string, LiveSubscription> subscriptions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private long lastSend;

    public LiveConnection(WebSocket socket, IServiceProvider services, DateTimeOffset deadline)
    {
        this.socket = socket;
        this.deadline = deadline;
        hub = services.GetRequiredService<LiveHub>();
        options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SparkLiveOptions>>().Value;
        queryLoader = services.GetRequiredService<IQueryLoader>();
        modelLoader = services.GetRequiredService<IModelLoader>();
        indexCatalog = services.GetRequiredService<IIndexCatalog>();
        store = services.GetRequiredService<IDocumentStore>();
        time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        Scopes = services.GetRequiredService<IServiceScopeFactory>();
        Json = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        Logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<LiveConnection>();
    }

    /// <summary>The HTTP JSON options, boundary net included: a pushed object has the fetched object's wire form.</summary>
    public JsonSerializerOptions Json { get; }
    public IServiceScopeFactory Scopes { get; }
    public ILogger Logger { get; }
    public CancellationToken Aborted => lifetime.Token;

    /// <summary>Evaluations run and their total time, process-wide (spike S8 numbers).</summary>
    internal static long Evaluations, EvaluationTicks, FramesSent, BytesSent;

    public void Wake()
    {
        if (wake.CurrentCount == 0)
            wake.Release();
    }

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, lifetime.Token);
        lastSend = time.GetTimestamp();
        // Same execution context as the request, so IHttpContextAccessor resolves the socket's context in
        // every scope the worker creates.
        var worker = Task.Run(() => WorkAsync(linked.Token), CancellationToken.None);
        try
        {
            await ReceiveAsync(linked.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            lifetime.Cancel();
            try { await worker; } catch { /* already logged */ }
            List<LiveSubscription> remaining;
            lock (subscriptions)
            {
                remaining = [.. subscriptions.Values];
                subscriptions.Clear();
            }
            foreach (var subscription in remaining)
                await EndAsync(subscription, removed: true);
        }
    }

    // ---- receiving ----------------------------------------------------------------------------------------------

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State == WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                if (message.WrittenCount + received.Count > options.MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Frame too large", CancellationToken.None);
                    return;
                }
                message.Write(buffer.AsSpan(0, received.Count));
                if (!received.EndOfMessage)
                    continue;

                if (received.MessageType == WebSocketMessageType.Text)
                    await HandleAsync(message.WrittenMemory, cancellationToken);
                message.Clear();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> text, CancellationToken cancellationToken)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            await SendAsync(new JsonObject { ["type"] = "error", ["code"] = LiveCodes.Invalid, ["message"] = "Not JSON" }, cancellationToken);
            return;
        }

        var op = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
        var sub = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sub", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrEmpty(sub) || sub.Length > 64)
        {
            await SendAsync(new JsonObject { ["type"] = "error", ["code"] = LiveCodes.Invalid, ["message"] = "Every operation names its subscription in \"sub\" (1–64 characters)" }, cancellationToken);
            return;
        }

        switch (op)
        {
            case "subscribe":
                await SubscribeAsync(sub, root, cancellationToken);
                break;
            case "update":
                await UpdateAsync(sub, root, cancellationToken);
                break;
            case "unsubscribe":
                LiveSubscription? existing;
                lock (subscriptions)
                {
                    if (subscriptions.Remove(sub, out existing)) { }
                }
                if (existing is not null)
                    await EndAsync(existing, removed: true);
                break;
            default:
                await SendAsync(ErrorFrame(sub, LiveCodes.Invalid, $"Unknown op '{op}'; expected subscribe, update or unsubscribe"), cancellationToken);
                break;
        }
    }

    private async Task SubscribeAsync(string sub, JsonElement root, CancellationToken cancellationToken)
    {
        int count;
        bool duplicate;
        lock (subscriptions)
        {
            count = subscriptions.Count;
            duplicate = subscriptions.ContainsKey(sub);
        }
        if (duplicate)
        {
            await SendAsync(ErrorFrame(sub, LiveCodes.Invalid, $"Subscription '{sub}' already exists"), cancellationToken);
            return;
        }
        if (count >= options.MaxSubscriptionsPerSocket)
        {
            await SendAsync(ErrorFrame(sub, LiveCodes.Limit, $"At most {options.MaxSubscriptionsPerSocket} subscriptions per connection"), cancellationToken);
            return;
        }

        LiveSubscription? subscription;
        try
        {
            subscription = Create(sub, root);
        }
        catch (JsonException)
        {
            await SendAsync(ErrorFrame(sub, LiveCodes.Invalid, "Malformed subscription"), cancellationToken);
            return;
        }

        if (subscription is null)
        {
            // Unknown, forbidden and not live are one answer: the per-subscribe replacement of the handshake 404
            // the streaming endpoint gave, so the existence oracle stays closed.
            await SendAsync(ErrorFrame(sub, LiveCodes.Unavailable, LiveCodes.UnavailableMessage), cancellationToken);
            return;
        }

        lock (subscriptions)
            subscriptions[sub] = subscription;
        await hub.RegisterAsync(subscription);
        subscription.MarkDirty(null);
    }

    private LiveSubscription? Create(string sub, JsonElement root)
    {
        if (root.TryGetProperty("po", out var poElement) && poElement.ValueKind == JsonValueKind.Object)
        {
            var request = poElement.Deserialize<PersistentObjectReferenceRequest>(Json) ?? throw new JsonException();
            request.RetryResults = null;
            request.Etag = null;
            var type = SparkRequestType.Resolve(modelLoader, request);
            if (type is not { Live: true } || string.IsNullOrEmpty(request.Id))
                return null;
            return new LivePersistentObjectSubscription(this, sub, request);
        }

        if (root.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.Object)
        {
            var request = Query(queryElement);
            var query = string.IsNullOrEmpty(request.QueryId) ? null : queryLoader.ResolveQuery(request.QueryId);
            if (query is not { Live: true })
                return null;
            return query.IsAppStream
                ? new LiveAppStreamSubscription(this, sub, request, query)
                : new LiveQuerySubscription(this, sub, request, LiveQueryCollections.Resolve(query, modelLoader, indexCatalog, store));
        }

        throw new JsonException();
    }

    private ExecuteQueryRequest Query(JsonElement element)
    {
        var request = element.Deserialize<ExecuteQueryRequest>(Json) ?? throw new JsonException();
        // A query hook cannot prompt from a push: there is nobody to answer.
        request.RetryResults = null;
        return request;
    }

    private async Task UpdateAsync(string sub, JsonElement root, CancellationToken cancellationToken)
    {
        LiveSubscription? subscription;
        lock (subscriptions)
            subscriptions.TryGetValue(sub, out subscription);

        ExecuteQueryRequest? request = null;
        try
        {
            if (root.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.Object)
                request = Query(queryElement);
        }
        catch (JsonException)
        {
        }

        if (subscription is null || request is null || !await subscription.UpdateAsync(request))
        {
            await SendAsync(ErrorFrame(sub, LiveCodes.Invalid, "Only an existing query subscription can be updated, with a \"query\""), cancellationToken);
            return;
        }
        subscription.MarkDirty(null);
    }

    // ---- the worker ---------------------------------------------------------------------------------------------

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        var revalidateAt = time.GetUtcNow() + options.Revalidate;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (time.GetUtcNow() >= deadline)
                {
                    // The principal frozen at the handshake is as old as it may get: the client reconnects with
                    // its current cookie, which the authentication middleware validates afresh.
                    await CloseAsync((WebSocketCloseStatus)LiveCodes.CloseReauthenticate, "Re-authenticate");
                    return;
                }

                if (time.GetUtcNow() >= revalidateAt)
                {
                    revalidateAt = time.GetUtcNow() + options.Revalidate;
                    foreach (var subscription in Snapshot())
                        subscription.MarkDirty(null);
                }

                TimeSpan? nextDue = null;
                foreach (var subscription in Snapshot())
                {
                    if (subscription.StaleRetryAt is { } retryAt)
                    {
                        // A page read before its indexes caught up: re-run it once the back-off passed.
                        var until = retryAt - time.GetUtcNow();
                        if (until <= TimeSpan.Zero)
                            subscription.FireStaleRetry();
                        else
                            nextDue = nextDue is null || until < nextDue ? until : nextDue;
                    }
                    if (!subscription.IsDirty)
                        continue;
                    var since = subscription.LastRun == 0 ? TimeSpan.MaxValue : time.GetElapsedTime(subscription.LastRun);
                    if (since < options.Throttle)
                    {
                        var due = options.Throttle - since;
                        nextDue = nextDue is null || due < nextDue ? due : nextDue;
                        continue;
                    }
                    await EvaluateAsync(subscription, cancellationToken);
                    if (subscription.StaleRetryAt is { } scheduled)
                    {
                        // The evaluation just scheduled a re-read: sleep no longer than until it is due.
                        var until = scheduled - time.GetUtcNow();
                        nextDue = nextDue is null || until < nextDue ? until : nextDue;
                    }
                }

                var silence = time.GetElapsedTime(Interlocked.Read(ref lastSend));
                if (silence >= options.Heartbeat)
                {
                    await SendAsync(new JsonObject { ["type"] = "ping" }, cancellationToken);
                    silence = TimeSpan.Zero;
                }

                var wait = Min(options.Heartbeat - silence, deadline - time.GetUtcNow(), revalidateAt - time.GetUtcNow(), nextDue);
                await wake.WaitAsync(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or SlowConsumerException)
        {
            lifetime.Cancel();
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b, TimeSpan c, TimeSpan? d)
    {
        var min = a < b ? a : b;
        min = c < min ? c : min;
        return d is { } due && due < min ? due : min;
    }

    private List<LiveSubscription> Snapshot()
    {
        lock (subscriptions)
            return [.. subscriptions.Values];
    }

    private async Task EvaluateAsync(LiveSubscription subscription, CancellationToken cancellationToken)
    {
        var collections = subscription.TakeDirty();
        subscription.LastRun = time.GetTimestamp();
        var started = Stopwatch.GetTimestamp();
        var caughtUp = true;
        LiveOutcome outcome;
        try
        {
            // A query re-run straight after a write would read its index stale; wait (bounded) until the
            // indexes over the changed collections caught up. Cheap when they already have: one statistics call.
            if (collections.Count > 0 && subscription is LiveQuerySubscription)
            {
                // The callback also fires for an index that went away under the wait, which then goes on with
                // whatever replaced it; only the final outcome says whether the read may be stale.
                caughtUp = await SparkIndexWait.WaitAsync(store, store.Database, collections, options.IndexWait,
                    (ex, index, elapsed) => Logger.LogDebug(ex,
                        "Live subscription {Subscription}: waiting for index {Index} ended after {Elapsed} ms",
                        subscription.Id, index, (long)elapsed.TotalMilliseconds),
                    cancellationToken);
                var waited = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (caughtUp)
                    Logger.LogDebug("Live subscription {Subscription}: the indexes over [{Collections}] caught up after {Elapsed} ms",
                        subscription.Id, string.Join(", ", collections), waited);
                else if (!cancellationToken.IsCancellationRequested)
                    Logger.LogWarning("Live subscription {Subscription}: the indexes over [{Collections}] did not catch up within {Elapsed} ms; the page is read now and read again once they have",
                        subscription.Id, string.Join(", ", collections), waited);
            }

            // A fresh scope per evaluation: the request-scoped row-filter memo, group snapshot, deleted mode and
            // session are new each time, so a revoked right or membership applies to this push, and the session
            // keeps its 30-request cap.
            await using var scope = Scopes.CreateAsyncScope();
            outcome = await subscription.EvaluateAsync(scope.ServiceProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SparkRetryActionException)
        {
            outcome = new LiveOutcome(ErrorFrame(subscription.Id, LiveCodes.Failed, "This read asks a question and cannot be watched live"), Ends: true);
        }
        catch (Exception ex)
        {
            // R2-M1: never the exception text over the wire.
            Logger.LogError(ex, "Live subscription {Subscription} failed", subscription.Id);
            outcome = new LiveOutcome(ErrorFrame(subscription.Id, LiveCodes.Failed, "Live update failed"), Ends: true);
        }
        finally
        {
            Interlocked.Increment(ref Evaluations);
            Interlocked.Add(ref EvaluationTicks, Stopwatch.GetTimestamp() - started);
        }

        if (!outcome.Ends && collections.Count > 0 && subscription is LiveQuerySubscription)
        {
            // Read before the indexes caught up: what was diffed (and sent, or found unchanged) may be stale, and
            // no other trigger is coming for it. Re-run it, bounded by a back-off that ends at Revalidate.
            if (caughtUp)
                subscription.ClearStaleRetry();
            else
                subscription.ScheduleStaleRetry(collections, time.GetUtcNow(), options.Throttle, options.Revalidate);
        }

        if (outcome.Frame is { } frame)
            await SendAsync(frame, cancellationToken);
        if (outcome.Ends)
        {
            lock (subscriptions)
            {
                if (subscriptions.TryGetValue(subscription.Id, out var current) && ReferenceEquals(current, subscription))
                    subscriptions.Remove(subscription.Id);
            }
            await EndAsync(subscription, removed: true);
        }
    }

    private async Task EndAsync(LiveSubscription subscription, bool removed)
    {
        if (removed)
            hub.Unregister(subscription);
        try
        {
            await subscription.DisposeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Disposing live subscription {Subscription} failed", subscription.Id);
        }
    }

    // ---- sending ------------------------------------------------------------------------------------------------

    private static JsonObject ErrorFrame(string sub, string code, string message)
        => new() { ["type"] = "error", ["sub"] = sub, ["code"] = code, ["message"] = message };

    private async Task SendAsync(JsonObject frame, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString(Json));
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.SendTimeout);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client that does not read is not allowed to make the server hold its frames.
                Logger.LogInformation("Live connection closed as a slow consumer: a frame was not taken within {Timeout}", options.SendTimeout);
                socket.Abort();
                throw new SlowConsumerException();
            }
            Interlocked.Exchange(ref lastSend, time.GetTimestamp());
            Interlocked.Increment(ref FramesSent);
            Interlocked.Add(ref BytesSent, bytes.Length);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        await sendLock.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            sendLock.Release();
        }
        // The client answers the close; if it does not, the receive loop ends with the request.
        lifetime.CancelAfter(TimeSpan.FromSeconds(5));
    }

    private sealed class SlowConsumerException : Exception;
}
