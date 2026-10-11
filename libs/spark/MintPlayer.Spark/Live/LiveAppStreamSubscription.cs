using System.Globalization;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Live;

/// <summary>
/// A live query whose rows come from an app-stream method (<see cref="LiveQueryArgs"/>), for data that is not
/// in RavenDB. The method runs in the background for as long as the subscription lives; each yield replaces
/// the rows (or upserts one), and the next evaluation secures, projects, searches, sorts and pages them.
/// </summary>
/// <remarks>
/// <para>
/// Fixes the streaming queries this replaces: a row missing from the next whole-set yield is removed
/// (defect 1); paging, sorting, search and the total count are applied server-side (the client kept and
/// filtered the whole set); the type-level <c>Query</c> right and row security are re-checked on every
/// evaluation, in a fresh scope, not every tenth batch on a frozen row-filter memo; and a revoked right ends
/// the subscription with <c>unavailable</c> instead of closing the socket with 1000, which left the grid
/// silently stale (defect 4).
/// </para>
/// <para>The method is not started until the viewer is known to hold the <c>Query</c> right: it is app code
/// with its own cost.</para>
/// </remarks>
internal sealed class LiveAppStreamSubscription(LiveConnection connection, string id, ExecuteQueryRequest request, SparkQuery query)
    : LiveSubscription(connection, id)
{
    private readonly object rowsGate = new();
    private ExecuteQueryRequest request = request;
    private List<object>? rows;
    private Dictionary<string, object>? byId;
    private Exception? failure;
    private bool started;
    private AsyncServiceScope? streamScope;
    private IAsyncDocumentSession? streamSession;
    private CancellationTokenSource? streamCancellation;
    private Task? pump;
    private LiveAppStreamMethod? method;

    public override async ValueTask<bool> UpdateAsync(ExecuteQueryRequest next)
    {
        // The method got the old page, sort and search as arguments: restart it with the new ones.
        await StopAsync();
        request = next;
        Previous = null; // always answered with a full result, as LiveQuerySubscription
        return true;
    }

    public override async Task<LiveOutcome> EvaluateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var definition = query.EntityType is null ? null : services.GetRequiredService<IModelLoader>().GetEntityTypeByName(query.EntityType);
        if (definition is null
            || !await services.GetRequiredService<IPermissionService>().IsAllowedAsync("Query", definition.Name, cancellationToken))
            return new LiveOutcome(Error(LiveCodes.Unavailable, LiveCodes.UnavailableMessage), Ends: true);

        if (failure is not null)
        {
            Connection.Logger.LogError(failure, "The app stream behind live query {Query} failed", query.Name);
            return new LiveOutcome(Error(LiveCodes.Failed, "Live update failed"), Ends: true);
        }

        var entityType = SparkTypeResolver.ResolveClrType(definition.ClrType);
        if (entityType is null)
            return new LiveOutcome(Error(LiveCodes.Unavailable, LiveCodes.UnavailableMessage), Ends: true);

        if (!started)
        {
            var parent = await services.GetRequiredService<IQueryReadPath>().ResolveParentAsync(request);
            if (parent is { Found: false })
                return new LiveOutcome(Error(LiveCodes.Unavailable, LiveCodes.UnavailableMessage), Ends: true);
            if (InvalidSort(definition) is { } invalid)
                return new LiveOutcome(Error(LiveCodes.Invalid, invalid), Ends: true);
            if (!Start(parent?.Value, entityType))
                return new LiveOutcome(Error(LiveCodes.Failed, "Live update failed"), Ends: true);
            return LiveOutcome.Unchanged;
        }

        List<object>? current;
        lock (rowsGate)
            current = rows is null ? null : [.. rows];
        if (current is null)
            return LiveOutcome.Unchanged;

        // The framework's own reads run on a fresh session per evaluation, capped like any request, so the
        // identity map is bounded to one evaluation.
        using var session = services.GetRequiredService<IDocumentStore>().OpenAsyncSession();
        services.GetRequiredService<IRowPolicyRequestState>().IsSubQuery = !string.IsNullOrEmpty(request.ParentId);
        var secured =await services.GetRequiredService<IRowSecurityGate>().ApplyAsync(current, new RowSecurityContext
        {
            Session = session,
            Definition = definition,
            EntityType = entityType,
            ResultType = method!.ElementType,
            Action = "Query",
            // No fan-out to collapse: an app stream has no index page.
            DedupeById = false,
            CancellationToken = cancellationToken,
        });

        var rights = services.GetService<IAttributeRightsEnforcement>();
        var surface = rights is null ? definition : await rights.ForQueryAsync(definition, cancellationToken);
        var columns = QueryResultProjector.BuildColumns(surface, query);
        var items = await QueryResultProjector.ToItemsAsync(session, secured, columns, query.Name, cancellationToken);

        var skip = Math.Max(0, request.Skip ?? 0);
        var take = Math.Clamp(request.Take ?? 50, 1, QueryReadPath.MaxTake);
        var shaped = Shape(items, request.Search, Sort(), skip, take, out var total);

        var next = Serialize(new QueryResult { Columns = columns, Items = shaped, TotalItems = total, Skip = skip, Take = take }, Connection.Json);
        var diff = LiveDiff.Query(Previous, next);
        Previous = next;
        return diff is { } d ? new LiveOutcome(Frame(d.Type, d.Payload, d.Type == "result" ? "result" : "patch"), false) : LiveOutcome.Unchanged;
    }

    private IReadOnlyList<SortColumn> Sort()
        => request.SortColumns is { Length: > 0 } overrides ? overrides : query.SortColumns;

    /// <summary>The allow-list <c>/execute</c> applies: the type's attributes and the query's declared sort.</summary>
    private string? InvalidSort(EntityTypeDefinition definition)
    {
        if (request.SortColumns is not { Length: > 0 } overrides)
            return null;
        var allowed = new HashSet<string>(definition.Attributes.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        allowed.UnionWith(query.SortColumns.Select(s => s.Property));
        var unknown = overrides.Where(o => !allowed.Contains(o.Property)).Select(o => o.Property).ToArray();
        if (unknown.Length > 0)
            return $"Unknown sort column(s): {string.Join(", ", unknown)}";
        return overrides.FirstOrDefault(o => !SortColumn.IsValidDirection(o.Direction)) is { } bad
            ? $"Unknown sort direction '{bad.Direction}' for {bad.Property}; expected 'asc' or 'desc'."
            : null;
    }

    private bool Start(PersistentObject? parent, Type entityType)
    {
        started = true;
        var scope = Connection.Scopes.CreateAsyncScope();
        streamScope = scope;
        var actions = scope.ServiceProvider.GetRequiredService<IActionsResolver>().ResolveForType(entityType);
        method = LiveAppStreamMethod.Resolve(actions.GetType(), query.Source[(query.Source.IndexOf('.') + 1)..]);
        if (method is null)
        {
            // Refused at startup (LiveStartup stamps IsAppStream only for a method with this signature); reaching
            // here means the model changed under a running process.
            Connection.Logger.LogError("Live query {Query}: {Actions} has no app-stream method for source {Source}", query.Name, actions.GetType().Name, query.Source);
            return false;
        }

        // #239 M4: a stream open for minutes is not "a request" — its request count would sail past RavenDB's
        // 30-cap purely from doing its job — so the method's own session is uncapped.
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().OpenAsyncSession();
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;
        streamSession = session;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Connection.Aborted);
        streamCancellation = cancellation;

        var args = new LiveQueryArgs
        {
            Query = query,
            Parent = parent,
            Search = request.Search,
            Skip = Math.Max(0, request.Skip ?? 0),
            Take = Math.Clamp(request.Take ?? 50, 1, QueryReadPath.MaxTake),
            SortColumns = Sort(),
            Session = session,
            CancellationToken = cancellation.Token,
        };
        var stream = method;
        pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in stream.InvokeAsync(actions, args, cancellation.Token))
                {
                    lock (rowsGate)
                    {
                        if (stream.IsSingleItemStream)
                        {
                            byId ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                            foreach (var row in batch)
                                byId[IdOf(row)] = row;
                            rows = [.. byId.Values];
                        }
                        else
                        {
                            rows = [.. batch];
                        }
                    }
                    MarkDirty(null);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
                MarkDirty(null);
            }
        });
        return true;
    }

    private static string IdOf(object row)
        => row.GetType().GetProperty("Id")?.GetValue(row) as string
           ?? throw new InvalidOperationException($"A row of type {row.GetType().Name} yielded by an app stream has no string Id.");

    /// <summary>Search, sort and page the secured rows in memory: an app stream has no database to do it.</summary>
    internal static IReadOnlyList<QueryResultItem> Shape(IReadOnlyList<QueryResultItem> items, string? search, IReadOnlyList<SortColumn> sort, int skip, int take, out int total)
    {
        IEnumerable<QueryResultItem> rows = items;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(item => (item.Breadcrumb?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || item.Values.Any(v => Text(v.Value)?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
        }

        IOrderedEnumerable<QueryResultItem>? ordered = null;
        foreach (var column in sort)
        {
            object? Key(QueryResultItem item) => item.Values.FirstOrDefault(v => string.Equals(v.Key, column.Property, StringComparison.OrdinalIgnoreCase))?.Value;
            var descending = string.Equals(column.Direction, "desc", StringComparison.OrdinalIgnoreCase);
            ordered = ordered is null
                ? descending ? rows.OrderByDescending(Key, ValueComparer.Instance) : rows.OrderBy(Key, ValueComparer.Instance)
                : descending ? ordered.ThenByDescending(Key, ValueComparer.Instance) : ordered.ThenBy(Key, ValueComparer.Instance);
        }

        var list = (ordered ?? rows).ToList();
        total = list.Count;
        return [.. list.Skip(skip).Take(take)];
    }

    private static string? Text(object? value)
        => value switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

    private sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null || y is null)
                return x is null ? (y is null ? 0 : -1) : 1;
            if (x.GetType() == y.GetType() && x is IComparable comparable)
                return comparable.CompareTo(y);
            if (IsNumber(x) && IsNumber(y))
                return Convert.ToDecimal(x, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(y, CultureInfo.InvariantCulture));
            return string.Compare(Text(x), Text(y), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumber(object value)
            => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    private async Task StopAsync()
    {
        started = false;
        streamCancellation?.Cancel();
        if (pump is not null)
        {
            try { await pump; } catch { /* reported through failure */ }
        }
        streamSession?.Dispose();
        if (streamScope is { } scope)
            await scope.DisposeAsync();
        streamCancellation?.Dispose();
        pump = null;
        streamSession = null;
        streamScope = null;
        streamCancellation = null;
        lock (rowsGate)
        {
            rows = null;
            byId = null;
        }
    }

    public override async ValueTask DisposeAsync() => await StopAsync();
}
