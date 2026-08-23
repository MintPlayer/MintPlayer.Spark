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
}

[Register(typeof(IRefreshInvoker), ServiceLifetime.Scoped)]
internal partial class RefreshInvoker : IRefreshInvoker
{
    [Inject] private readonly IActionsResolver actionsResolver;

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
