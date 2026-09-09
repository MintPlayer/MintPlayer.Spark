using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Dispatches <c>OnDeleteRowAsync</c> to a row type's actions class — the mirror of
/// <see cref="INewInvoker"/>, resolved the same way and for the same reasons.
/// </summary>
public interface IDeleteRowInvoker
{
    /// <summary>
    /// Runs the row type's removal hook. A no-op when the actions class does not override it.
    /// </summary>
    /// <param name="entityType">The CLR row type whose actions class owns the hook.</param>
    /// <param name="row">The row being removed, read from the stored parent.</param>
    /// <param name="parent">The parent whose collection the row is leaving.</param>
    /// <param name="asDetailAttribute">Name of that collection.</param>
    /// <param name="rowKey">The row's <c>[ValueKey]</c> value.</param>
    /// <param name="parameters">Client-supplied arguments; null is normalised to empty.</param>
    /// <exception cref="Abstractions.SparkValidationException">
    /// Propagated from a hook that refuses. Deliberately not caught here: the endpoint turns it into
    /// a 400 the user can read, and swallowing it would turn a refusal into a silent success.
    /// </exception>
    Task InvokeAsync(
        Type entityType,
        PersistentObject row,
        PersistentObject parent,
        string asDetailAttribute,
        string rowKey,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken);

    /// <summary>Whether the row type's actions class overrides the removal hook.</summary>
    bool HasDeleteRowHook(Type entityType);
}

[Register(typeof(IDeleteRowInvoker), ServiceLifetime.Scoped)]
internal partial class DeleteRowInvoker : IDeleteRowInvoker
{
    [Inject] private readonly IActionsResolver actionsResolver;

    public bool HasDeleteRowHook(Type entityType) => ResolveHook(entityType) is not null;

    public async Task InvokeAsync(
        Type entityType,
        PersistentObject row,
        PersistentObject parent,
        string asDetailAttribute,
        string rowKey,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        var method = ResolveHook(entityType);
        if (method is null)
            return;

        var args = CreateArgs(entityType, row, parent, asDetailAttribute, rowKey, parameters, cancellationToken);
        var actions = actionsResolver.ResolveForType(entityType);

        // The hook returns Task, never Task<T>, so this cast is total.
        await (Task)method.Invoke(actions, [args])!;
    }

    private static object CreateArgs(
        Type entityType,
        PersistentObject row,
        PersistentObject parent,
        string asDetailAttribute,
        string rowKey,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        var ctor = ReflectionCache.GetOrAdd<Type, ConstructorInfo>(
            entityType,
            static t => typeof(SparkDeleteRowArgs<>).MakeGenericType(t)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single());

        return ctor.Invoke([row, parent, asDetailAttribute, rowKey, parameters, cancellationToken]);
    }

    /// <summary>
    /// Returns the hook only when the actions class actually overrides it. A method still declared
    /// on <see cref="DefaultPersistentObjectActions{T}"/> — or resolved to the interface's own
    /// default implementation — is the framework's no-op.
    /// </summary>
    private MethodInfo? ResolveHook(Type entityType)
    {
        var actionsType = actionsResolver.ResolveForType(entityType).GetType();
        return ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("DeleteRowInvoker.OnDeleteRowAsync", actionsType, entityType),
            static k =>
            {
                var method = k.Actions.GetMethod(
                    "OnDeleteRowAsync",
                    [typeof(SparkDeleteRowArgs<>).MakeGenericType(k.Entity)]);

                var declaring = method?.DeclaringType;
                if (declaring is null)
                    return null;

                var isBaseDeclaration = declaring.IsGenericType
                    && declaring.GetGenericTypeDefinition() == typeof(DefaultPersistentObjectActions<>);

                return isBaseDeclaration ? null : method;
            });
    }
}
