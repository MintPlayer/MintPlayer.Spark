using System.Security.Claims;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Finds and runs the persistence interceptors (#482) that govern an entity type, and carries the
/// per-request state the load paths share (which instances were already materialized).
/// </summary>
internal interface ISparkInterceptorPipeline
{
    /// <summary>
    /// The interceptors of phase <typeparamref name="TInterceptor"/> that govern <paramref name="entityType"/>, in
    /// registration order — before-interceptors <see cref="InterceptorStage.Default"/> first, then
    /// <see cref="InterceptorStage.Finalize"/>. For a <see cref="PersistentObjectOperation.Sync"/> only the interceptors
    /// that opted in (<see cref="ISparkInterceptor.HandlesSync"/>).
    /// </summary>
    IReadOnlyList<TInterceptor> For<TInterceptor>(Type entityType, PersistentObjectOperation? operation = null) where TInterceptor : class, ISparkInterceptor;

    /// <summary>The caller, when there is an HTTP request.</summary>
    ClaimsPrincipal? User { get; }

    /// <summary>Whether the caller is the system rather than a viewer.</summary>
    bool IsSystemContext { get; }

    /// <summary>
    /// Runs <see cref="IAfterMaterialize"/> interceptors (F1) for each of <paramref name="entities"/> not already
    /// intercepted in this request (by reference).
    /// </summary>
    Task RunAfterMaterializeAsync(Type entityType, IAsyncDocumentSession session, IEnumerable<object> entities, MaterializeReason reason);

    /// <summary>Runs <see cref="IAfterLoad"/> interceptors for each loaded object.</summary>
    Task RunAfterLoadAsync(Type entityType, IAsyncDocumentSession session, IReadOnlyList<PersistentObject> objects);

    /// <summary>
    /// Runs after-interceptors, each isolated: a failure is logged and neither turns the committed write
    /// into an error nor skips the interceptors after it.
    /// </summary>
    Task RunIsolatedAsync<TInterceptor>(IReadOnlyList<TInterceptor> interceptors, Func<TInterceptor, ValueTask> call, Type entityType, string? id) where TInterceptor : class, ISparkInterceptor;

    /// <summary>
    /// Stores one outbox message per applicable durable after-commit interceptor (#482, D17) in the write's
    /// own session, after the last before-interceptor, so it commits with the write or not at all.
    /// </summary>
    Task EnqueueCommittedAsync(SparkInterceptorContext context, string id, PersistentObjectOperation operation, bool isDelete, bool isReplaced, string? previousChangeVector);
}

[Register(typeof(ISparkInterceptorPipeline), ServiceLifetime.Scoped)]
internal sealed partial class SparkInterceptorPipeline : ISparkInterceptorPipeline
{
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;
    [Inject] private readonly ILogger<SparkInterceptorPipeline>? logger;

    // Entity instances whose materialize interceptors already ran in this request (F1): the Update endpoint
    // pre-reads in the session the save reloads from, so the reload hands back the same instance.
    private readonly HashSet<object> materialized = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Type Phase, Type Entity, bool Sync), object> forType = [];
    private readonly Dictionary<Type, ISparkInterceptor?> actionsInterceptors = [];

    public ClaimsPrincipal? User => httpContextAccessor?.HttpContext?.User;

    public bool IsSystemContext => Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor);

    public IReadOnlyList<TInterceptor> For<TInterceptor>(Type entityType, PersistentObjectOperation? operation = null) where TInterceptor : class, ISparkInterceptor
    {
        var sync = operation == PersistentObjectOperation.Sync;
        if (forType.TryGetValue((typeof(TInterceptor), entityType, sync), out var cached))
            return (IReadOnlyList<TInterceptor>)cached;

        var applicable = new List<TInterceptor>();
        foreach (var interceptor in serviceProvider.GetServices<TInterceptor>())
        {
            if (Applies(interceptor, entityType, sync))
                applicable.Add(interceptor);
        }

        // The type's own Actions class may implement interceptors for it — no registration needed, and one
        // instance per request serves all its phases (ApiToken's shown-once secret crosses from its
        // before-save to its after-save). After the registered interceptors of its stage; registered as an interceptor
        // too, it still runs once.
        if (ActionsOf(entityType) is TInterceptor own && Applies(own, entityType, sync)
            && !applicable.Any(h => h.GetType() == own.GetType()))
            applicable.Add(own);

        // OrderBy is stable: within a stage, interceptors keep the order they were registered in.
        IReadOnlyList<TInterceptor> result = applicable switch
        {
            [] => [],
            List<TInterceptor> list when typeof(TInterceptor) == typeof(IBeforeSave) => [.. list.OrderBy(h => ((IBeforeSave)h).Stage)],
            List<TInterceptor> list when typeof(TInterceptor) == typeof(IBeforeDelete) => [.. list.OrderBy(h => ((IBeforeDelete)h).Stage)],
            _ => applicable,
        };

        if (typeof(TInterceptor) == typeof(IDeleteReplacement) && result.Count > 1)
            throw new InvalidOperationException(
                $"More than one {nameof(IDeleteReplacement)} governs '{entityType.FullName}': " +
                string.Join(", ", result.Select(h => h.GetType().FullName)) + ". At most one may decide whether its deletes are replaced.");

        forType[(typeof(TInterceptor), entityType, sync)] = result;
        return result;
    }

    private static bool Applies(ISparkInterceptor interceptor, Type entityType, bool sync)
        => (!sync || interceptor.HandlesSync)
           && ReflectionCache.GetOrAdd<(string Op, Type Interceptor, Type Entity), bool>(
               ("SparkInterceptor.AppliesTo", interceptor.GetType(), entityType),
               k => interceptor.AppliesTo(k.Entity));

    /// <summary>The entity type's Actions class, resolved once per request, when it implements an interceptor.</summary>
    private ISparkInterceptor? ActionsOf(Type entityType)
    {
        if (!actionsInterceptors.TryGetValue(entityType, out var interceptor))
        {
            interceptor = serviceProvider.GetService<IActionsResolver>()?.ResolveForType(entityType) as ISparkInterceptor;
            actionsInterceptors[entityType] = interceptor;
        }
        return interceptor;
    }

    public async Task RunAfterMaterializeAsync(Type entityType, IAsyncDocumentSession session, IEnumerable<object> entities, MaterializeReason reason)
    {
        var interceptors = For<IAfterMaterialize>(entityType);
        if (interceptors.Count == 0)
            return;

        var user = User;
        var system = IsSystemContext;
        foreach (var entity in entities)
        {
            if (entity is null || !materialized.Add(entity))
                continue;

            var context = new MaterializeContext
            {
                EntityType = entityType,
                Entity = entity,
                Session = session,
                Reason = reason,
                User = user,
                IsSystemContext = system,
            };
            foreach (var interceptor in interceptors)
                await interceptor.OnAfterMaterializeAsync(context);
        }
    }

    public async Task RunAfterLoadAsync(Type entityType, IAsyncDocumentSession session, IReadOnlyList<PersistentObject> objects)
    {
        var interceptors = For<IAfterLoad>(entityType);
        if (interceptors.Count == 0 || objects.Count == 0)
            return;

        var user = User;
        var system = IsSystemContext;
        foreach (var obj in objects)
        {
            var context = new LoadContext { EntityType = entityType, PersistentObject = obj, Session = session, User = user, IsSystemContext = system };
            foreach (var interceptor in interceptors)
                await interceptor.OnAfterLoadAsync(context);
        }
    }

    public async Task EnqueueCommittedAsync(
        SparkInterceptorContext context, string id, PersistentObjectOperation operation, bool isDelete, bool isReplaced, string? previousChangeVector)
    {
        IReadOnlyList<ISparkInterceptor> durable = isDelete
            ? For<IAfterDeleteCommitted>(context.EntityType, operation)
            : For<IAfterSaveCommitted>(context.EntityType, operation);
        if (durable.Count == 0)
            return;

        // The startup check covers registered interceptors; this covers an Actions class implementing one.
        var outbox = serviceProvider.GetService<ISparkAfterCommitOutbox>()
            ?? throw new InvalidOperationException(SparkCommittedInterceptorsStartupCheck.MissingOutboxMessage(durable.Select(h => h.GetType())));

        // A server-assigned id ("Orders|") is only known after the commit, and the message is written in it.
        if (id.EndsWith('|'))
            throw new InvalidOperationException(
                $"'{context.EntityType.FullName}' uses server-assigned ids, which durable after-commit interceptors cannot address: " +
                "the message is written in the same commit, before the id exists. Assign ids on the client (the default).");

        var currentUser = serviceProvider.GetService<Abstractions.Authentication.ISparkCurrentUser>();
        var change = new SparkCommittedChange
        {
            EntityType = context.EntityType.FullName!,
            Id = id,
            Operation = operation,
            IsReplaced = isReplaced,
            UserId = currentUser is { IsAuthenticated: true } ? currentUser.Id : null,
            IsSystemContext = context.IsSystemContext,
            OccurredAt = (serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(),
            PreviousChangeVector = previousChangeVector,
            Facts = new Dictionary<string, string>(context.Facts, StringComparer.Ordinal),
        };

        foreach (var interceptor in durable)
            await outbox.EnqueueAsync(context.Session, new SparkAfterCommitWork { InterceptorType = interceptor.GetType().FullName!, IsDelete = isDelete, Change = change });
    }

    public async Task RunIsolatedAsync<TInterceptor>(IReadOnlyList<TInterceptor> interceptors, Func<TInterceptor, ValueTask> call, Type entityType, string? id) where TInterceptor : class, ISparkInterceptor
    {
        foreach (var interceptor in interceptors)
        {
            try
            {
                await call(interceptor);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "{Interceptor} failed after the {EntityType} '{Id}' write was committed. The write stands; the interceptors after it still ran.",
                    interceptor.GetType().Name, entityType.Name, id);
            }
        }
    }
}
