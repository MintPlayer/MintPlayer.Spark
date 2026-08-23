using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Dispatches <c>OnRefreshAsync</c> to an entity's actions class.
/// <para>
/// The hook is <see cref="NoInterfaceMemberAttribute">off the interface</see> deliberately — nothing
/// outside the framework dispatches a refresh, so declaring it on
/// <see cref="IPersistentObjectActions{T}"/> would break every hand-written implementer to buy
/// nothing. That choice is what makes this reflection necessary, and it is the same trade
/// <c>GetDefaultIncludes</c> and the row-security hooks already make.
/// </para>
/// </summary>
public interface IRefreshInvoker
{
    /// <summary>
    /// Runs the entity's refresh hook against <paramref name="persistentObject"/>, mutating it in
    /// place. A no-op when the actions class does not override the hook.
    /// </summary>
    /// <param name="entityType">The CLR entity type whose actions class owns the hook.</param>
    /// <param name="persistentObject">The in-progress object to reshape.</param>
    /// <param name="triggeredBy">
    /// Name of the attribute whose change asked for the refresh. Matched by name rather than id
    /// because scaffolded attributes carry no id. <see langword="null"/> — or a name the object does
    /// not carry — still runs the hook, with a null <c>args.Attribute</c>.
    /// </param>
    Task InvokeAsync(
        Type entityType,
        PersistentObject persistentObject,
        string? triggeredBy,
        bool isNew,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the entity's actions class overrides the refresh hook. Lets callers skip the whole
    /// path — and lets the model-verify gate report a declared trigger nothing implements.
    /// </summary>
    bool HasRefreshHook(Type entityType);

    /// <summary>
    /// Builds the object a save must actually be judged against: scaffolded from the model, carrying
    /// the client's values, with every declared refresh trigger applied.
    /// <para>
    /// This is what makes the feature enforceable rather than decorative. Validation reads
    /// <c>IsRequired</c> and <c>Rules</c> off the object, so if the server did not re-derive them it
    /// would be enforcing the model's rules while the user was shown the hook's — and any client
    /// that simply never called <c>/refresh</c> would escape the hook entirely.
    /// </para>
    /// <para>
    /// The hook runs once per triggering attribute, in model order, against the same accumulating
    /// object. Running it once with no attribute would be cheaper and wrong: handlers branch on
    /// <c>args.Attribute</c>, so a single null-attribute pass executes none of their branches.
    /// </para>
    /// </summary>
    Task<PersistentObject> BuildEffectiveAsync(
        EntityTypeDefinition entityType,
        PersistentObject submitted,
        CancellationToken cancellationToken);
}

[Register(typeof(IRefreshInvoker), ServiceLifetime.Scoped)]
internal partial class RefreshInvoker : IRefreshInvoker
{
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IEffectiveObjectFactory effectiveObjectFactory;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    [Inject] private readonly Raven.Client.Documents.Session.IAsyncDocumentSession session;
    [Inject] private readonly ILogger<RefreshInvoker> logger;

    /// <summary>
    /// Advisory ceiling for the save-time re-derivation. Deliberately per-save rather than per
    /// trigger: a type with several triggers runs the hook several times, and the budget that
    /// matters is the one the whole save spends.
    /// </summary>
    private const int SaveRefreshRequestBudget = 30;

    public async Task<PersistentObject> BuildEffectiveAsync(
        EntityTypeDefinition entityType,
        PersistentObject submitted,
        CancellationToken cancellationToken)
    {
        var effective = effectiveObjectFactory.Build(entityType, submitted);

        var triggers = effectiveObjectFactory.TriggeringAttributeNames(entityType);
        if (triggers.Count == 0)
            return effective;

        var clrType = typeResolver.Resolve(entityType.ClrType);
        if (clrType is null || !HasRefreshHook(clrType))
            return effective;

        // The hook runs once per trigger here, so a save costs several times what one refresh does
        // — on top of everything the save itself spends. Without this a type with a handful of
        // triggers and a hook that looks anything up can exhaust the session budget and fail the
        // save, which would make declaring a trigger quietly reduce how much an entity's save path
        // is allowed to do.
        using var _ = session.IgnoreMaxRequests(SaveRefreshRequestBudget, logger);

        var isNew = string.IsNullOrEmpty(submitted.Id);
        foreach (var trigger in triggers)
            await InvokeAsync(clrType, effective, trigger, isNew, cancellationToken);

        return effective;
    }

    public bool HasRefreshHook(Type entityType) => ResolveHook(entityType) is not null;

    public async Task InvokeAsync(
        Type entityType,
        PersistentObject persistentObject,
        string? triggeredBy,
        bool isNew,
        CancellationToken cancellationToken)
    {
        var method = ResolveHook(entityType);
        if (method is null)
            return;

        var attribute = triggeredBy is null
            ? null
            : persistentObject.Attributes.FirstOrDefault(
                a => string.Equals(a.Name, triggeredBy, StringComparison.Ordinal));

        var args = CreateArgs(entityType, persistentObject, attribute, isNew, cancellationToken);
        var actions = actionsResolver.ResolveForType(entityType);

        // The hook returns Task, never Task<T>, so this cast is total. A null would mean the method
        // was resolved from something that is not the hook — worth failing loudly rather than
        // silently skipping the developer's business logic.
        await (Task)method.Invoke(actions, [args])!;
    }

    private static object CreateArgs(
        Type entityType,
        PersistentObject persistentObject,
        PersistentObjectAttribute? attribute,
        bool isNew,
        CancellationToken cancellationToken)
    {
        var ctor = ReflectionCache.GetOrAdd<Type, ConstructorInfo>(
            entityType,
            static t => typeof(SparkRefreshArgs<>).MakeGenericType(t)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single());

        return ctor.Invoke([persistentObject, attribute, isNew, cancellationToken]);
    }

    /// <summary>
    /// Returns the hook only when the actions class actually overrides it. A method still declared
    /// on <see cref="DefaultPersistentObjectActions{T}"/> is the framework's own no-op: invoking it
    /// costs a reflection call to accomplish nothing, and reporting it as present would make the
    /// verify gate accept a trigger nobody implemented.
    /// </summary>
    private MethodInfo? ResolveHook(Type entityType)
    {
        var actionsType = actionsResolver.ResolveForType(entityType).GetType();
        return ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("RefreshInvoker.OnRefreshAsync", actionsType, entityType),
            static k =>
            {
                var method = k.Actions.GetMethod(
                    "OnRefreshAsync",
                    [typeof(SparkRefreshArgs<>).MakeGenericType(k.Entity)]);

                var declaring = method?.DeclaringType;
                if (declaring is null)
                    return null;

                var isBaseDeclaration = declaring.IsGenericType
                    && declaring.GetGenericTypeDefinition() == typeof(DefaultPersistentObjectActions<>);

                return isBaseDeclaration ? null : method;
            });
    }
}
