using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Dispatches <c>OnNewAsync</c> to an entity's actions class.
/// <para>
/// Resolved by exact signature rather than by bare name, the way
/// <see cref="IRefreshInvoker"/> does — and for the same reason the args object exists at all: the
/// hook's inputs are expected to grow, and a signature-exact lookup plus a one-element invoke stays
/// correct when they do.
/// </para>
/// </summary>
public interface INewInvoker
{
    /// <summary>
    /// Runs the entity's construction hook against <paramref name="persistentObject"/>, mutating it
    /// in place. A no-op when the actions class does not override the hook.
    /// </summary>
    /// <param name="entityType">The CLR entity type whose actions class owns the hook.</param>
    /// <param name="persistentObject">The freshly scaffolded object to shape.</param>
    /// <param name="parent">The object this one is being created from, if any.</param>
    /// <param name="asDetailParent">
    /// The object whose <c>AsDetail</c> collection the row is being added to; null for a standalone
    /// New. Usually the same instance as <paramref name="parent"/> — the two differ in meaning, not
    /// normally in identity, and the narrow one is what says the parent owns the save.
    /// </param>
    /// <param name="asDetailAttribute">Name of the collection the row is being added to, if any.</param>
    /// <param name="parameters">Client-supplied arguments; null is normalised to empty.</param>
    Task InvokeAsync(
        Type entityType,
        PersistentObject persistentObject,
        PersistentObject? parent,
        PersistentObject? asDetailParent,
        string? asDetailAttribute,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the entity's actions class overrides the construction hook. Lets a caller skip the
    /// whole path for the common case of a type with no defaults to set.
    /// </summary>
    bool HasNewHook(Type entityType);
}

[Register(typeof(INewInvoker), ServiceLifetime.Scoped)]
internal partial class NewInvoker : INewInvoker
{
    [Inject] private readonly IActionsResolver actionsResolver;

    public bool HasNewHook(Type entityType) => ResolveHook(entityType) is not null;

    public async Task InvokeAsync(
        Type entityType,
        PersistentObject persistentObject,
        PersistentObject? parent,
        PersistentObject? asDetailParent,
        string? asDetailAttribute,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        var method = ResolveHook(entityType);
        if (method is null)
            return;

        var args = CreateArgs(
            entityType, persistentObject, parent, asDetailParent, asDetailAttribute, parameters, cancellationToken);
        var actions = actionsResolver.ResolveForType(entityType);

        // The hook returns Task, never Task<T>, so this cast is total. A null would mean the method
        // was resolved from something that is not the hook — worth failing loudly rather than
        // silently skipping the developer's business logic.
        // ⚠️ `DoNotWrapExceptions` is load-bearing, not tidiness. Without it `MethodBase.Invoke`
        // wraps whatever the hook throws in a TargetInvocationException, so the endpoint's
        // `catch (SparkValidationException)` matches nothing and a hook that politely refuses
        // surfaces to the user as a 500. It also keeps the hook's own stack trace intact.
        await (Task)method.Invoke(
            actions, BindingFlags.DoNotWrapExceptions, binder: null, parameters: [args], culture: null)!;
    }

    private static object CreateArgs(
        Type entityType,
        PersistentObject persistentObject,
        PersistentObject? parent,
        PersistentObject? asDetailParent,
        string? asDetailAttribute,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        // ⚠️ The key is a tuple, not a bare Type. `GetOrAdd<TKey, TValue>` is ONE dictionary per
        // (TKey, TValue) pair, so every invoker keying a ConstructorInfo by entity type shares it —
        // and the first one to run for a given type hands its constructor to the others. That is not
        // hypothetical: it made `DeleteRowInvoker` invoke `SparkNewArgs`'s constructor with a delete
        // hook's arguments, failing on the third one. The discriminator is what keeps them apart.
        var ctor = ReflectionCache.GetOrAdd<(string Op, Type Entity), ConstructorInfo>(
            ("NewInvoker.args", entityType),
            static k => typeof(SparkNewArgs<>).MakeGenericType(k.Entity)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single());

        return ctor.Invoke(
            [persistentObject, parent, asDetailParent, asDetailAttribute, parameters, cancellationToken]);
    }

    /// <summary>
    /// Returns the hook only when the actions class actually overrides it. A method still declared
    /// on <see cref="DefaultPersistentObjectActions{T}"/> — or resolved to the interface's own
    /// default implementation — is the framework's no-op, and invoking it costs a reflection call to
    /// accomplish nothing.
    /// </summary>
    private MethodInfo? ResolveHook(Type entityType)
    {
        var actionsType = actionsResolver.ResolveForType(entityType).GetType();
        return ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("NewInvoker.OnNewAsync", actionsType, entityType),
            static k =>
            {
                var method = k.Actions.GetMethod(
                    "OnNewAsync",
                    [typeof(SparkNewArgs<>).MakeGenericType(k.Entity)]);

                var declaring = method?.DeclaringType;
                if (declaring is null)
                    return null;

                var isBaseDeclaration = declaring.IsGenericType
                    && declaring.GetGenericTypeDefinition() == typeof(DefaultPersistentObjectActions<>);

                return isBaseDeclaration ? null : method;
            });
    }
}
