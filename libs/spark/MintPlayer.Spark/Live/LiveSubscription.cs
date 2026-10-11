using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Live;

/// <summary>What one evaluation produced: a frame to send (or none), and whether the subscription ends.</summary>
internal readonly record struct LiveOutcome(JsonObject? Frame, bool Ends)
{
    public static LiveOutcome Unchanged => default;
}

/// <summary>
/// One subscription on one socket: what it watches, whether it is dirty, and the last state the client was
/// sent. Evaluated only by its connection's single worker, so its state needs no lock beyond the dirty bit.
/// </summary>
internal abstract class LiveSubscription(LiveConnection connection, string id) : IAsyncDisposable
{
    private readonly object dirtyGate = new();
    private readonly HashSet<string> changedCollections = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    public string Id { get; } = id;
    public LiveConnection Connection { get; } = connection;

    /// <summary>Document ids whose change makes this subscription dirty.</summary>
    public virtual IReadOnlyCollection<string> Documents => [];

    /// <summary>Collections whose change makes this subscription dirty.</summary>
    public virtual IReadOnlyCollection<string> Collections => [];

    /// <summary>When the last evaluation started (a <see cref="TimeProvider"/> timestamp; 0 = never).</summary>
    public long LastRun { get; set; }

    /// <summary>The wire form the client holds, which the next evaluation is diffed against.</summary>
    protected JsonObject? Previous { get; set; }

    public bool IsDirty
    {
        get { lock (dirtyGate) return dirty; }
    }

    /// <summary>
    /// Marks the subscription for re-evaluation. <paramref name="collection"/> names a changed collection whose
    /// indexes the evaluation first waits for, so a re-run query does not read them stale.
    /// </summary>
    public void MarkDirty(string? collection) => MarkDirty(collection, change: true);

    private void MarkDirty(string? collection, bool change)
    {
        lock (dirtyGate)
        {
            dirty = true;
            changedSinceTake |= change;
            if (collection is not null)
                changedCollections.Add(collection);
        }
        OnDirty();
    }

    private bool changedSinceTake;

    /// <summary>
    /// Whether the evaluation that last took the dirty bit was only a stale re-read (<see cref="FireStaleRetry"/>),
    /// with no new change since the one before it. Only then does the re-read back-off double.
    /// </summary>
    public bool LastTakeWasRetryOnly { get; private set; }

    /// <summary>Wakes the connection's worker.</summary>
    protected virtual void OnDirty() => Connection.Wake();

    /// <summary>Clears the dirty bit and returns the collections to wait for.</summary>
    public IReadOnlySet<string> TakeDirty()
    {
        lock (dirtyGate)
        {
            dirty = false;
            LastTakeWasRetryOnly = !changedSinceTake;
            changedSinceTake = false;
            if (changedCollections.Count == 0)
                return EmptySet;
            var taken = new HashSet<string>(changedCollections, StringComparer.OrdinalIgnoreCase);
            changedCollections.Clear();
            return taken;
        }
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    // ---- re-reading a page read before its indexes caught up ----------------------------------------------------

    /// <summary>The shortest first back-off, so a zero throttle cannot turn the retry into a busy loop.</summary>
    private static readonly TimeSpan MinimumStaleBackoff = TimeSpan.FromMilliseconds(100);

    private IReadOnlySet<string>? staleCollections;
    private TimeSpan staleBackoff;

    /// <summary>When a re-evaluation of a page read stale is due (null = none pending).</summary>
    public DateTimeOffset? StaleRetryAt { get; private set; }

    /// <summary>
    /// The last evaluation read before the indexes over <paramref name="collections"/> caught up with the
    /// change that triggered it (the index wait's bound ran out). Its diff may have found nothing to send, or
    /// only part of the change — a row's new values but not its new place — and without another write nothing
    /// re-runs it until <see cref="SparkLiveOptions.Revalidate"/>. So it is re-run, waiting again, after a
    /// back-off that starts at <paramref name="first"/> and doubles while the re-reads stay stale with no new
    /// change in between (a new change starts over at <paramref name="first"/>: it is a new episode, not the same
    /// index still lagging). Bounded: once the back-off reaches <paramref name="ceiling"/> (Revalidate), no
    /// further retry is scheduled and Revalidate takes over.
    /// </summary>
    /// <returns>false when the back-off reached the ceiling and no retry was scheduled.</returns>
    public bool ScheduleStaleRetry(IReadOnlySet<string> collections, DateTimeOffset now, TimeSpan first, TimeSpan ceiling)
    {
        staleBackoff = staleBackoff == TimeSpan.Zero || !LastTakeWasRetryOnly
            ? (first > MinimumStaleBackoff ? first : MinimumStaleBackoff)
            : staleBackoff * 2;
        if (staleBackoff >= ceiling)
        {
            ClearStaleRetry();
            return false;
        }
        var merged = new HashSet<string>(collections, StringComparer.OrdinalIgnoreCase);
        if (staleCollections is not null)
            merged.UnionWith(staleCollections);
        staleCollections = merged;
        StaleRetryAt = now + staleBackoff;
        return true;
    }

    /// <summary>An evaluation read its indexes caught up: no retry is pending and the back-off starts over.</summary>
    public void ClearStaleRetry()
    {
        staleCollections = null;
        staleBackoff = TimeSpan.Zero;
        StaleRetryAt = null;
    }

    /// <summary>The retry is due: marks the subscription dirty with the collections whose indexes it waits for.</summary>
    public void FireStaleRetry()
    {
        var collections = staleCollections;
        StaleRetryAt = null;
        staleCollections = null;
        if (collections is null)
            return;
        foreach (var collection in collections)
            MarkDirty(collection, change: false);
    }

    /// <summary>Re-runs the read under the viewer's rights in <paramref name="services"/> (a fresh scope) and diffs it.</summary>
    public abstract Task<LiveOutcome> EvaluateAsync(IServiceProvider services, CancellationToken cancellationToken);

    /// <summary>Replaces a query subscription's request (page, sort, search); false for a kind that has none.</summary>
    public virtual ValueTask<bool> UpdateAsync(ExecuteQueryRequest request) => ValueTask.FromResult(false);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected JsonObject Frame(string type, JsonNode? payload = null, string? key = null)
    {
        var frame = new JsonObject { ["type"] = type, ["sub"] = Id };
        if (payload is not null)
            frame[key ?? type] = payload;
        return frame;
    }

    protected JsonObject Error(string code, string message)
        => new() { ["type"] = "error", ["sub"] = Id, ["code"] = code, ["message"] = message };

    protected static JsonObject Serialize<T>(T value, JsonSerializerOptions options)
        => JsonSerializer.SerializeToNode(value, options)!.AsObject();
}

/// <summary>A persistent object on its detail or edit page (<c>po</c> → <c>poPatch</c>… → <c>gone</c>).</summary>
internal sealed class LivePersistentObjectSubscription(LiveConnection connection, string id, PersistentObjectReferenceRequest request)
    : LiveSubscription(connection, id)
{
    private readonly string[] documents = [request.Id!];

    public override IReadOnlyCollection<string> Documents => documents;

    public override async Task<LiveOutcome> EvaluateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var po = await services.GetRequiredService<IPersistentObjectReadPath>().LoadAsync(request, cancellationToken);

        // Deleted and forbidden are one answer (#453): the subscription ends either way, and the client
        // cannot tell which it was.
        if (po is null)
            return new LiveOutcome(Frame("gone"), Ends: true);

        var next = Serialize(po, Connection.Json);
        var diff = LiveDiff.PersistentObject(Previous, next);
        Previous = next;
        return diff is { } d ? new LiveOutcome(Frame(d.Type, d.Payload, d.Type == "po" ? "po" : "patch"), false) : LiveOutcome.Unchanged;
    }
}

/// <summary>A page of a RavenDB-backed query (<c>result</c> → <c>patch</c>…), re-run when its collection changes.</summary>
internal sealed class LiveQuerySubscription(LiveConnection connection, string id, ExecuteQueryRequest request, IReadOnlyCollection<string> collections)
    : LiveSubscription(connection, id)
{
    private ExecuteQueryRequest request = request;

    public override IReadOnlyCollection<string> Collections => collections;

    public override ValueTask<bool> UpdateAsync(ExecuteQueryRequest next)
    {
        request = next;
        // An update is always answered: the next evaluation sends a full result, so the grid knows its
        // new page, sort or search took effect even when the rows it shows did not change.
        Previous = null;
        return ValueTask.FromResult(true);
    }

    public override async Task<LiveOutcome> EvaluateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var outcome = await services.GetRequiredService<IQueryReadPath>().ExecuteAsync(request, cancellationToken);
        if (outcome.Result is not { } result)
        {
            // An unknown, forbidden or no longer readable query (or parent) is one answer, as on /execute; a
            // malformed request (an unknown sort column) says what was wrong, which the caller already knows.
            return new LiveOutcome(
                outcome.StatusCode == StatusCodes.Status400BadRequest
                    ? Error(LiveCodes.Invalid, outcome.Error ?? "Invalid request")
                    : Error(LiveCodes.Unavailable, LiveCodes.UnavailableMessage),
                Ends: true);
        }

        var next = Serialize(result, Connection.Json);
        var diff = LiveDiff.Query(Previous, next);
        Previous = next;
        return diff is { } d ? new LiveOutcome(Frame(d.Type, d.Payload, d.Type == "result" ? "result" : "patch"), false) : LiveOutcome.Unchanged;
    }
}

/// <summary>The codes an <c>error</c> frame carries; the subscription ends with each, the socket stays open.</summary>
internal static class LiveCodes
{
    /// <summary>Unknown, forbidden or not live, alike (#453): the answer says nothing about existence.</summary>
    public const string Unavailable = "unavailable";
    public const string UnavailableMessage = "Not available for live updates";
    /// <summary>A malformed request: what was wrong is said, because the caller sent it.</summary>
    public const string Invalid = "invalid";
    /// <summary>Over <see cref="SparkLiveOptions.MaxSubscriptionsPerSocket"/>.</summary>
    public const string Limit = "limit";
    /// <summary>A server error, logged; the message is generic.</summary>
    public const string Failed = "failed";

    /// <summary>The login ticket expired or the socket reached its maximum lifetime: reconnect now with the current cookie.</summary>
    public const int CloseReauthenticate = 4401;
    /// <summary>Over <see cref="SparkLiveOptions.MaxSocketsPerUser"/>: do not reconnect until a socket closes.</summary>
    public const int CloseTooManySockets = 4429;
}
