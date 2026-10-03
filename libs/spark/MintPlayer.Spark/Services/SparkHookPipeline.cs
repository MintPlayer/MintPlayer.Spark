using System.Security.Claims;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Finds and runs the persistence hooks (#482) that govern an entity type, and carries the
/// per-request state the load paths share (which instances were already materialized).
/// </summary>
internal interface ISparkHookPipeline
{
    /// <summary>
    /// The hooks of phase <typeparamref name="THook"/> that govern <paramref name="entityType"/>, in
    /// registration order — before-hooks <see cref="HookStage.Default"/> first, then
    /// <see cref="HookStage.Finalize"/>. For a <see cref="PersistentObjectOperation.Sync"/> only the hooks
    /// that opted in (<see cref="ISparkHook.HandlesSync"/>).
    /// </summary>
    IReadOnlyList<THook> For<THook>(Type entityType, PersistentObjectOperation? operation = null) where THook : class, ISparkHook;

    /// <summary>The caller, when there is an HTTP request.</summary>
    ClaimsPrincipal? User { get; }

    /// <summary>Whether the caller is the system rather than a viewer.</summary>
    bool IsSystemContext { get; }

    /// <summary>
    /// Runs <see cref="IAfterMaterialize"/> hooks (F1) for each of <paramref name="entities"/> not already
    /// hooked in this request (by reference).
    /// </summary>
    Task RunAfterMaterializeAsync(Type entityType, IAsyncDocumentSession session, IEnumerable<object> entities, MaterializeReason reason);

    /// <summary>Runs <see cref="IAfterLoad"/> hooks for each loaded object.</summary>
    Task RunAfterLoadAsync(Type entityType, IAsyncDocumentSession session, IReadOnlyList<PersistentObject> objects);

    /// <summary>
    /// Runs after-hooks, each isolated: a failure is logged and neither turns the committed write
    /// into an error nor skips the hooks after it.
    /// </summary>
    Task RunIsolatedAsync<THook>(IReadOnlyList<THook> hooks, Func<THook, ValueTask> call, Type entityType, string? id) where THook : class, ISparkHook;

    /// <summary>
    /// Stores one outbox message per applicable durable after-commit hook (#482, D17) in the write's
    /// own session, after the last before-hook, so it commits with the write or not at all.
    /// </summary>
    Task EnqueueCommittedAsync(SparkHookContext context, string id, PersistentObjectOperation operation, bool isDelete, bool isReplaced, string? previousChangeVector);
}

[Register(typeof(ISparkHookPipeline), ServiceLifetime.Scoped)]
internal sealed partial class SparkHookPipeline : ISparkHookPipeline
{
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;
    [Inject] private readonly ILogger<SparkHookPipeline>? logger;

    // Entity instances whose materialize hooks already ran in this request (F1): the Update endpoint
    // pre-reads in the session the save reloads from, so the reload hands back the same instance.
    private readonly HashSet<object> materialized = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Type Phase, Type Entity, bool Sync), object> forType = [];
    private readonly Dictionary<Type, ISparkHook?> actionsHooks = [];

    public ClaimsPrincipal? User => httpContextAccessor?.HttpContext?.User;

    public bool IsSystemContext => Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor);

    public IReadOnlyList<THook> For<THook>(Type entityType, PersistentObjectOperation? operation = null) where THook : class, ISparkHook
    {
        var sync = operation == PersistentObjectOperation.Sync;
        if (forType.TryGetValue((typeof(THook), entityType, sync), out var cached))
            return (IReadOnlyList<THook>)cached;

        var applicable = new List<THook>();
        foreach (var hook in serviceProvider.GetServices<THook>())
        {
            if (Applies(hook, entityType, sync))
                applicable.Add(hook);
        }

        // The type's own Actions class may implement hooks for it — no registration needed, and one
        // instance per request serves all its phases (ApiToken's shown-once secret crosses from its
        // before-save to its after-save). After the registered hooks of its stage; registered as a hook
        // too, it still runs once.
        if (ActionsOf(entityType) is THook own && Applies(own, entityType, sync)
            && !applicable.Any(h => h.GetType() == own.GetType()))
            applicable.Add(own);

        // OrderBy is stable: within a stage, hooks keep the order they were registered in.
        IReadOnlyList<THook> result = applicable switch
        {
            [] => [],
            List<THook> list when typeof(THook) == typeof(IBeforeSave) => [.. list.OrderBy(h => ((IBeforeSave)h).Stage)],
            List<THook> list when typeof(THook) == typeof(IBeforeDelete) => [.. list.OrderBy(h => ((IBeforeDelete)h).Stage)],
            _ => applicable,
        };

        if (typeof(THook) == typeof(IDeleteReplacement) && result.Count > 1)
            throw new InvalidOperationException(
                $"More than one {nameof(IDeleteReplacement)} governs '{entityType.FullName}': " +
                string.Join(", ", result.Select(h => h.GetType().FullName)) + ". At most one may decide whether its deletes are replaced.");

        forType[(typeof(THook), entityType, sync)] = result;
        return result;
    }

    private static bool Applies(ISparkHook hook, Type entityType, bool sync)
        => (!sync || hook.HandlesSync)
           && ReflectionCache.GetOrAdd<(string Op, Type Hook, Type Entity), bool>(
               ("SparkHook.AppliesTo", hook.GetType(), entityType),
               k => hook.AppliesTo(k.Entity));

    /// <summary>The entity type's Actions class, resolved once per request, when it implements a hook.</summary>
    private ISparkHook? ActionsOf(Type entityType)
    {
        if (!actionsHooks.TryGetValue(entityType, out var hook))
        {
            hook = serviceProvider.GetService<IActionsResolver>()?.ResolveForType(entityType) as ISparkHook;
            actionsHooks[entityType] = hook;
        }
        return hook;
    }

    public async Task RunAfterMaterializeAsync(Type entityType, IAsyncDocumentSession session, IEnumerable<object> entities, MaterializeReason reason)
    {
        var hooks = For<IAfterMaterialize>(entityType);
        if (hooks.Count == 0)
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
            foreach (var hook in hooks)
                await hook.OnAfterMaterializeAsync(context);
        }
    }

    public async Task RunAfterLoadAsync(Type entityType, IAsyncDocumentSession session, IReadOnlyList<PersistentObject> objects)
    {
        var hooks = For<IAfterLoad>(entityType);
        if (hooks.Count == 0 || objects.Count == 0)
            return;

        var user = User;
        var system = IsSystemContext;
        foreach (var obj in objects)
        {
            var context = new LoadContext { EntityType = entityType, PersistentObject = obj, Session = session, User = user, IsSystemContext = system };
            foreach (var hook in hooks)
                await hook.OnAfterLoadAsync(context);
        }
    }

    public async Task EnqueueCommittedAsync(
        SparkHookContext context, string id, PersistentObjectOperation operation, bool isDelete, bool isReplaced, string? previousChangeVector)
    {
        IReadOnlyList<ISparkHook> durable = isDelete
            ? For<IAfterDeleteCommitted>(context.EntityType, operation)
            : For<IAfterSaveCommitted>(context.EntityType, operation);
        if (durable.Count == 0)
            return;

        // The startup check covers registered hooks; this covers an Actions class implementing one.
        var outbox = serviceProvider.GetService<ISparkAfterCommitOutbox>()
            ?? throw new InvalidOperationException(SparkCommittedHooksStartupCheck.MissingOutboxMessage(durable.Select(h => h.GetType())));

        // A server-assigned id ("Orders|") is only known after the commit, and the message is written in it.
        if (id.EndsWith('|'))
            throw new InvalidOperationException(
                $"'{context.EntityType.FullName}' uses server-assigned ids, which durable after-commit hooks cannot address: " +
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

        foreach (var hook in durable)
            await outbox.EnqueueAsync(context.Session, new SparkAfterCommitWork { HookType = hook.GetType().FullName!, IsDelete = isDelete, Change = change });
    }

    public async Task RunIsolatedAsync<THook>(IReadOnlyList<THook> hooks, Func<THook, ValueTask> call, Type entityType, string? id) where THook : class, ISparkHook
    {
        foreach (var hook in hooks)
        {
            try
            {
                await call(hook);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "{Hook} failed after the {EntityType} '{Id}' write was committed. The write stands; the hooks after it still ran.",
                    hook.GetType().Name, entityType.Name, id);
            }
        }
    }
}
