using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Runs <see cref="IPersistentObjectInterceptor"/>s (#460, item 1) and carries the per-request state
/// that lets <c>DatabaseAccess</c> and the base <c>DefaultPersistentObjectActions</c> hand a save or
/// delete to each other without a new parameter on any overridable hook.
/// </summary>
internal interface IPersistentObjectInterceptorPipeline
{
    /// <summary>
    /// The interceptors that govern <paramref name="entityType"/>, in before-hook order: ascending
    /// <see cref="IPersistentObjectInterceptor.Order"/>, registration order breaking ties (F5).
    /// After-hooks iterate it backwards.
    /// </summary>
    IReadOnlyList<IPersistentObjectInterceptor> For(Type entityType);

    /// <summary>
    /// Runs <see cref="IPersistentObjectInterceptor.OnAfterMaterializeAsync"/> (F1) for each of
    /// <paramref name="entities"/> not already hooked in this request (by reference), in before-hook order.
    /// </summary>
    Task RunAfterMaterializeAsync(Type entityType, IAsyncDocumentSession session, IEnumerable<object> entities, MaterializeReason reason);

    /// <summary>Whether any interceptor governing <paramref name="entityType"/> implements <see cref="IPersistentObjectInterceptor.OnAfterMaterializeAsync"/>.</summary>
    bool HasMaterializeHooks(Type entityType);

    /// <summary>Registers the save <c>DatabaseAccess</c> is about to hand to the Actions class.</summary>
    void BeginSave(SaveContext context, IReadOnlyList<IPersistentObjectInterceptor> interceptors);

    /// <summary>Ends it; true when the base <c>OnSaveAsync</c> ran the before-hooks.</summary>
    bool EndSave(SaveContext context);

    /// <summary>
    /// Called by the base <c>OnSaveAsync</c> between <c>OnBeforeSaveAsync</c> and WITH CHECK: runs the
    /// before-save interceptors for the save <paramref name="obj"/> belongs to. A no-op when the save
    /// did not come through <c>DatabaseAccess</c>.
    /// </summary>
    Task RunBeforeSaveAsync(PersistentObject obj, object entity);

    /// <summary>Marks the Actions class's <c>OnBeforeDeleteAsync</c> as already run for this entity.</summary>
    void MarkBeforeDeleteHandled(object entity);

    /// <summary>True (once) when <c>DatabaseAccess</c> already ran <c>OnBeforeDeleteAsync</c> for this entity.</summary>
    bool ConsumeBeforeDeleteHandled(object entity);
}

[Register(typeof(IPersistentObjectInterceptorPipeline), ServiceLifetime.Scoped)]
internal sealed partial class PersistentObjectInterceptorPipeline : IPersistentObjectInterceptorPipeline
{
    [Inject] private readonly IEnumerable<IPersistentObjectInterceptor>? interceptors;
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;

    private readonly Dictionary<PersistentObject, (SaveContext Context, IReadOnlyList<IPersistentObjectInterceptor> Interceptors, bool Ran)> saves
        = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> beforeDeleteHandled = new(ReferenceEqualityComparer.Instance);
    // Entity instances whose materialize hooks already ran in this request (F1): the Update endpoint
    // pre-reads in the session the save reloads from, so the reload hands back the same instance.
    private readonly HashSet<object> materialized = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, IReadOnlyList<IPersistentObjectInterceptor>> forType = [];

    public IReadOnlyList<IPersistentObjectInterceptor> For(Type entityType)
    {
        if (interceptors is null)
            return [];
        if (forType.TryGetValue(entityType, out var cached))
            return cached;

        List<IPersistentObjectInterceptor>? applicable = null;
        foreach (var interceptor in interceptors)
        {
            var applies = ReflectionCache.GetOrAdd<(string Op, Type Interceptor, Type Entity), bool>(
                ("PersistentObjectInterceptor.AppliesTo", interceptor.GetType(), entityType),
                k => interceptor.AppliesTo(k.Entity));
            if (applies)
                (applicable ??= []).Add(interceptor);
        }

        // Ordered by (Order, registration index) — OrderBy is stable, so equal orders keep the
        // order the interceptors were registered in (F5).
        IReadOnlyList<IPersistentObjectInterceptor> result = applicable is null
            ? []
            : [.. applicable.OrderBy(i => i.Order)];
        forType[entityType] = result;
        return result;
    }

    public async Task RunAfterMaterializeAsync(Type entityType, IAsyncDocumentSession session, IEnumerable<object> entities, MaterializeReason reason)
    {
        var applicable = For(entityType);
        if (applicable.Count == 0 || !HasMaterializeHooks(entityType))
            return;

        var user = httpContextAccessor?.HttpContext?.User;
        var system = Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor);
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
            foreach (var interceptor in applicable)
                await interceptor.OnAfterMaterializeAsync(context);
        }
    }

    public bool HasMaterializeHooks(Type entityType)
        => For(entityType).Any(i => ImplementsMaterialize(i.GetType()));

    /// <summary>Whether <paramref name="interceptorType"/> implements the hook rather than inheriting the no-op default.</summary>
    private static bool ImplementsMaterialize(Type interceptorType)
        => ReflectionCache.GetOrAdd<(string Op, Type Interceptor), bool>(
            ("PersistentObjectInterceptor.ImplementsMaterialize", interceptorType),
            static k =>
            {
                var map = k.Interceptor.GetInterfaceMap(typeof(IPersistentObjectInterceptor));
                for (var i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i].Name == nameof(IPersistentObjectInterceptor.OnAfterMaterializeAsync))
                        return map.TargetMethods[i].DeclaringType != typeof(IPersistentObjectInterceptor);
                }
                return false;
            });

    public void BeginSave(SaveContext context, IReadOnlyList<IPersistentObjectInterceptor> interceptors)
        => saves[context.PersistentObject] = (context, interceptors, false);

    public bool EndSave(SaveContext context)
        => saves.Remove(context.PersistentObject, out var state) && state.Ran;

    public async Task RunBeforeSaveAsync(PersistentObject obj, object entity)
    {
        if (!saves.TryGetValue(obj, out var state) || state.Ran)
            return;

        saves[obj] = state with { Ran = true };
        state.Context.Entity = entity;
        foreach (var interceptor in state.Interceptors)
            await interceptor.OnBeforeSaveAsync(state.Context);
    }

    public void MarkBeforeDeleteHandled(object entity) => beforeDeleteHandled.Add(entity);

    public bool ConsumeBeforeDeleteHandled(object entity) => beforeDeleteHandled.Remove(entity);
}
