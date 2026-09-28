using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Session;

using static MintPlayer.Spark.Services.SparkHookInvocation;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Asks an actions class's <c>OnDisableActionsAsync</c> hook (#460, D13) — at load, to fill
/// <c>DisabledActions</c>, and at submit, to refuse a disabled action.
/// </summary>
/// <remarks>
/// One implementation for both phases, so the question asked at submit is the question asked at
/// load: the same hook, the same target kinds, the same stored entity. The only difference is the
/// phase and the action name in the context.
/// </remarks>
internal interface IDisabledActionsEvaluator
{
    /// <summary>Load: asks the hook about a detail object and records the answer on it.</summary>
    Task ApplyOnLoadAsync(PersistentObject obj);

    /// <summary>Load: asks the hook about a query execution; null when nothing was withheld.</summary>
    Task<IReadOnlyList<string>?> EvaluateQueryOnLoadAsync(object? actions, SparkQuery query, PersistentObject? parent, string? parentType);

    /// <summary>
    /// Runs the batched hook over <paramref name="items"/> on <paramref name="actions"/> and returns
    /// the union of what it withheld, case-insensitively. Empty when there is no actions class.
    /// </summary>
    Task<IReadOnlySet<string>> EvaluateAsync(object? actions, IReadOnlyList<DisableActionsItem> items);

    /// <summary>
    /// Submit for one object of <paramref name="entityType"/>: throws
    /// <see cref="SparkActionDisabledException"/> when the hook disables any of
    /// <paramref name="refusedBy"/>. Skipped in the system context.
    /// </summary>
    Task EnsureEnabledAsync(Type entityType, string actionName, IReadOnlyList<string> refusedBy, string? id, object? entity);

    /// <summary>The stored entities behind <paramref name="ids"/> — one batched, session-cached load.</summary>
    Task<IReadOnlyDictionary<string, object>> LoadEntitiesAsync(Type entityType, IReadOnlyCollection<string> ids);

    /// <summary>The actions class for a type — by CLR type, or by name for a type without documents.</summary>
    object? ResolveActions(Type? clrType, string entityName);
}

/// <summary>The framework's own <see cref="IDisablable"/>: a set of names and nothing else.</summary>
internal sealed class DisabledActionSet : IDisablable
{
    private readonly List<string> names = [];

    public IReadOnlyList<string> Names => names;

    public void DisableActions(params string[] actionNames)
    {
        if (actionNames is null)
            return;

        foreach (var name in actionNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }
    }
}

[Register(typeof(IDisabledActionsEvaluator), ServiceLifetime.Scoped)]
internal partial class DisabledActionsEvaluator : IDisabledActionsEvaluator
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;

    public async Task ApplyOnLoadAsync(PersistentObject obj)
    {
        var definition = modelLoader.GetEntityType(obj.ObjectTypeId);
        if (definition is null)
            return;

        var clrType = typeResolver.Resolve(definition.ClrType);
        var actions = ResolveActions(clrType, definition.Name);
        if (actions is null)
            return;

        object? entity = null;
        if (clrType is not null && !string.IsNullOrEmpty(obj.Id))
        {
            // Session-cached: the load pipeline that produced `obj` already read this document.
            var loaded = await LoadEntitiesAsync(clrType, [obj.Id]);
            loaded.TryGetValue(obj.Id, out entity);
        }

        // The object itself is the target, as D13 describes: at load, what the hook withholds IS the
        // object's DisabledActions.
        await EvaluateAsync(actions,
        [
            new DisableActionsItem(obj, new DisableActionsContext
            {
                Phase = DisableActionsPhase.Load,
                TargetKind = DisableActionsTargetKind.PersistentObject,
                Id = obj.Id,
                Entity = entity,
            }),
        ]);
    }

    public Task<IReadOnlyList<string>?> EvaluateQueryOnLoadAsync(
        object? actions, SparkQuery query, PersistentObject? parent, string? parentType)
        => EvaluateQueryCoreAsync(actions, query, parent, parentType);

    /// <summary>
    /// Static so <c>QueryExecutor</c>, whose constructor is part of many tests' fixtures, can ask the
    /// hook with the actions instance it already resolved rather than growing a dependency.
    /// </summary>
    internal static async Task<IReadOnlyList<string>?> EvaluateQueryCoreAsync(
        object? actions, SparkQuery query, PersistentObject? parent, string? parentType)
    {
        if (actions is null)
            return null;

        var target = new DisabledActionSet();
        await EvaluateCoreAsync(actions,
        [
            new DisableActionsItem(target, new DisableActionsContext
            {
                Phase = DisableActionsPhase.Load,
                TargetKind = DisableActionsTargetKind.Query,
                Query = SparkQueryInfo.From(query),
                Parent = parent,
                ParentType = parentType,
            }),
        ]);

        return target.Names.Count == 0 ? null : target.Names;
    }

    public Task<IReadOnlySet<string>> EvaluateAsync(object? actions, IReadOnlyList<DisableActionsItem> items)
        => EvaluateCoreAsync(actions, items);

    internal static async Task<IReadOnlySet<string>> EvaluateCoreAsync(object? actions, IReadOnlyList<DisableActionsItem> items)
    {
        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (actions is null || items.Count == 0)
            return union;

        var hook = ResolveBatchedHook(actions.GetType());
        if (hook is null)
            return union;

        // HookInvoke: a hook that throws — including a retry prompt — must arrive as itself, not
        // wrapped in TargetInvocationException, or no typed catch in the endpoint matches it.
        if (hook.Invoke(actions, HookInvoke, binder: null, parameters: [items], culture: null) is Task task)
            await task;

        foreach (var item in items)
        {
            var names = item.Target switch
            {
                DisabledActionSet set => set.Names,
                PersistentObject po => po.DisabledActions,
                _ => null,
            };

            if (names is not null)
                union.UnionWith(names);
        }

        return union;
    }

    public async Task EnsureEnabledAsync(Type entityType, string actionName, IReadOnlyList<string> refusedBy, string? id, object? entity)
    {
        // Module sync and background work: no user is submitting anything, and a hook written for
        // users (a locked row withholds Edit) must not stop the system repairing that same row.
        if (Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor))
            return;

        var actions = actionsResolver.ResolveForType(entityType);
        var disabled = await EvaluateAsync(actions,
        [
            new DisableActionsItem(new DisabledActionSet(), new DisableActionsContext
            {
                Phase = DisableActionsPhase.Submit,
                TargetKind = DisableActionsTargetKind.PersistentObject,
                ActionName = actionName,
                Id = id,
                Entity = entity,
            }),
        ]);

        foreach (var name in refusedBy)
        {
            if (disabled.Contains(name))
                throw new SparkActionDisabledException(name);
        }
    }

    public async Task<IReadOnlyDictionary<string, object>> LoadEntitiesAsync(Type entityType, IReadOnlyCollection<string> ids)
        => await RowSecurity.LoadBaseDocumentsAsync(session, entityType, ids);

    public object? ResolveActions(Type? clrType, string entityName)
        => clrType is not null
            ? actionsResolver.ResolveForType(clrType)
            : actionsResolver.ResolveByEntityName(entityName);

    /// <summary>
    /// The interface's batched hook, invoked through the interface so a class that declares neither
    /// form still dispatches to the default, and one that overrides either form is honoured.
    /// </summary>
    private static MethodInfo? ResolveBatchedHook(Type actionsType)
        => ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("DisabledActions.BatchedHook", actionsType),
            static k =>
            {
                var contract = k.Type.GetInterfaces().FirstOrDefault(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPersistentObjectActions<>));

                return contract?.GetMethod(
                    nameof(IPersistentObjectActions<object>.OnDisableActionsAsync),
                    [typeof(IReadOnlyList<DisableActionsItem>)]);
            });
}
