using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using System.Reflection;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Row-level authorization gate: dispatches to an entity's <c>Actions.IsAllowedAsync(action, entity)</c>
/// hook via reflection. Fail-open on unknown shape (same convention as DatabaseAccess). Shared by
/// <see cref="ReferenceResolver"/> and the breadcrumb resolver so the per-document Read check (R2-H10)
/// has a single implementation.
/// </summary>
internal interface IRowSecurity
{
    Task<bool> IsAllowedAsync(Type entityType, string action, object entity);
}

[Register(typeof(IRowSecurity), ServiceLifetime.Scoped)]
internal partial class RowSecurity : IRowSecurity
{
    [Inject] private readonly IActionsResolver actionsResolver;

    public async Task<bool> IsAllowedAsync(Type entityType, string action, object entity)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var actionsType = actions.GetType();
        var method = ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("RowSecurity.IsAllowedAsync", actionsType, entityType),
            static k => k.Actions.GetMethod("IsAllowedAsync", [typeof(string), k.Entity]));
        if (method is null) return true;
        var task = (Task)method.Invoke(actions, [action, entity])!;
        await task;
        var resultProperty = task.GetType().GetProperty("Result");
        return (bool)resultProperty!.GetValue(task)!;
    }
}
