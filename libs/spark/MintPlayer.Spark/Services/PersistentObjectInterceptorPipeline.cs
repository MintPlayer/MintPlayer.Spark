using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Runs <see cref="IPersistentObjectInterceptor"/>s (#460, item 1) and carries the per-request state
/// that lets <c>DatabaseAccess</c> and the base <c>DefaultPersistentObjectActions</c> hand a save or
/// delete to each other without a new parameter on any overridable hook.
/// </summary>
internal interface IPersistentObjectInterceptorPipeline
{
    /// <summary>The interceptors that govern <paramref name="entityType"/>, in registration order.</summary>
    IReadOnlyList<IPersistentObjectInterceptor> For(Type entityType);

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

    private readonly Dictionary<PersistentObject, (SaveContext Context, IReadOnlyList<IPersistentObjectInterceptor> Interceptors, bool Ran)> saves
        = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> beforeDeleteHandled = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<IPersistentObjectInterceptor> For(Type entityType)
    {
        if (interceptors is null)
            return [];

        List<IPersistentObjectInterceptor>? applicable = null;
        foreach (var interceptor in interceptors)
        {
            var applies = ReflectionCache.GetOrAdd<(string Op, Type Interceptor, Type Entity), bool>(
                ("PersistentObjectInterceptor.AppliesTo", interceptor.GetType(), entityType),
                k => interceptor.AppliesTo(k.Entity));
            if (applies)
                (applicable ??= []).Add(interceptor);
        }
        return applicable ?? (IReadOnlyList<IPersistentObjectInterceptor>)[];
    }

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
