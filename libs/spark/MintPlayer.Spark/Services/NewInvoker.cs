using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;

using static MintPlayer.Spark.Services.SparkHookInvocation;

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
        CancellationToken cancellationToken,
        SparkNewSubQueryContext? subQuery = null,
        SparkNewReferenceContext? reference = null);

    /// <summary>
    /// Runs the construction hook of a type with no C# class: its name-resolved <c>{Name}Actions</c>,
    /// duck-typed like the virtual <c>OnLoadAsync</c> (<c>public Task OnNewAsync(SparkNewArgs args)</c>).
    /// A no-op when there is no such class or it has no <c>OnNewAsync</c>; a method named
    /// <c>OnNewAsync</c> with another shape throws, so a typo is not silently skipped (PRD D15).
    /// </summary>
    Task InvokeVirtualAsync(
        string entityTypeName,
        PersistentObject persistentObject,
        PersistentObject? parent,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken,
        SparkNewReferenceContext? reference = null);

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
        CancellationToken cancellationToken,
        SparkNewSubQueryContext? subQuery = null,
        SparkNewReferenceContext? reference = null)
    {
        // The base hook is skipped as a no-op — except for a New from a sub-query, where the base is
        // NOT a no-op: it fills the parent reference (#460, D19, owner refinement).
        var method = ResolveHook(entityType)
            ?? (subQuery is not null ? ResolveBaseHook(entityType) : null);
        if (method is null)
            return;

        var args = new SparkNewArgs(
            persistentObject, parent, asDetailParent, asDetailAttribute, parameters, subQuery, reference, cancellationToken);
        var actions = actionsResolver.ResolveForType(entityType);

        // The hook returns Task, never Task<T>, so this cast is total. A null would mean the method
        // was resolved from something that is not the hook — worth failing loudly rather than
        // silently skipping the developer's business logic.
        // ⚠️ `DoNotWrapExceptions` is load-bearing, not tidiness. Without it `MethodBase.Invoke`
        // wraps whatever the hook throws in a TargetInvocationException, so the endpoint's
        // `catch (SparkValidationException)` matches nothing and a hook that politely refuses
        // surfaces to the user as a 500. It also keeps the hook's own stack trace intact.
        await (Task)method.Invoke(
            actions, HookInvoke, binder: null, parameters: [args], culture: null)!;
    }

    public async Task InvokeVirtualAsync(
        string entityTypeName,
        PersistentObject persistentObject,
        PersistentObject? parent,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken,
        SparkNewReferenceContext? reference = null)
    {
        var actions = actionsResolver.ResolveByEntityName(entityTypeName);
        if (actions is null)
            return;

        var method = ReflectionCache.GetOrAdd<(string Op, Type Actions), MethodInfo?>(
            ("NewInvoker.VirtualOnNewAsync", actions.GetType()),
            static k =>
            {
                var method = k.Actions.GetMethod("OnNewAsync", [typeof(SparkNewArgs)]);
                if (method is not null && method.ReturnType == typeof(Task))
                    return method;
                if (k.Actions.GetMethods().Any(m => m.Name == "OnNewAsync"))
                    throw new InvalidOperationException(
                        $"'{k.Actions.FullName}' has an OnNewAsync that doesn't match the construction contract. " +
                        "Expected: 'Task OnNewAsync(SparkNewArgs args)'.");
                return null;
            });
        if (method is null)
            return;

        var args = new SparkNewArgs(
            persistentObject, parent, asDetailParent: null, asDetailAttribute: null, parameters, subQuery: null, reference, cancellationToken);
        await (Task)method.Invoke(actions, HookInvoke, binder: null, parameters: [args], culture: null)!;
    }

    /// <summary>
    /// The hook as the actions class exposes it, the base declaration included — for the sub-query
    /// New, where the base fills the parent reference. Null when the class does not derive from
    /// <see cref="DefaultPersistentObjectActions{T}"/> (an interface default has nothing to fill).
    /// </summary>
    private MethodInfo? ResolveBaseHook(Type entityType)
    {
        var actionsType = actionsResolver.ResolveForType(entityType).GetType();
        return ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("NewInvoker.OnNewAsync.Base", actionsType, entityType),
            static k => k.Actions.GetMethod("OnNewAsync", [typeof(SparkNewArgs)]));
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
                var method = k.Actions.GetMethod("OnNewAsync", [typeof(SparkNewArgs)]);

                var declaring = method?.DeclaringType;
                if (declaring is null)
                    return null;

                var isBaseDeclaration = declaring.IsGenericType
                    && declaring.GetGenericTypeDefinition() == typeof(DefaultPersistentObjectActions<>);

                return isBaseDeclaration ? null : method;
            });
    }
}
