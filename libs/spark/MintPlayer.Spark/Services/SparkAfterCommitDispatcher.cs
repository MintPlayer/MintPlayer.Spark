using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Runs one durable after-commit hook for one committed row (#482, D17) — what the outbox message's
/// handler calls, in a fresh DI scope outside any request.
/// </summary>
/// <remarks>
/// The hook is looked up by name among the registered hooks of its phase, then among the Actions
/// classes of the model's entity types: a name in a message never loads a type by itself.
/// </remarks>
[Register(typeof(ISparkAfterCommitDispatcher), ServiceLifetime.Scoped)]
internal sealed partial class SparkAfterCommitDispatcher : ISparkAfterCommitDispatcher
{
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    [Inject] private readonly IActionsResolver actionsResolver;

    public async Task<bool> DispatchAsync(SparkAfterCommitWork work, CancellationToken cancellationToken)
    {
        if (work.IsDelete)
        {
            if (Find<IAfterDeleteCommitted>(work) is not { } hook)
                return false;
            await hook.OnAfterDeleteCommittedAsync(work.Change, cancellationToken);
        }
        else
        {
            if (Find<IAfterSaveCommitted>(work) is not { } hook)
                return false;
            await hook.OnAfterSaveCommittedAsync(work.Change, cancellationToken);
        }
        return true;
    }

    private THook? Find<THook>(SparkAfterCommitWork work) where THook : class, ISparkHook
    {
        foreach (var hook in serviceProvider.GetServices<THook>())
        {
            if (hook.GetType().FullName == work.HookType)
                return hook;
        }

        // An Actions class implementing the hook for its own type: only for a type of the model.
        if (modelLoader.GetEntityTypeByClrType(work.Change.EntityType) is { } definition
            && typeResolver.Resolve(definition.ClrType) is { } entityType
            && actionsResolver.ResolveForType(entityType) is THook own
            && own.GetType().FullName == work.HookType)
            return own;

        return null;
    }
}

/// <summary>
/// A durable after-commit hook without an outbox is a startup error (#482, D17; owner decision
/// 2026-10-03): the alternative is a hook that silently never runs.
/// </summary>
internal static class SparkCommittedHooksStartupCheck
{
    public static void Run(IServiceProvider services)
    {
        // Asked of the container, not resolved: the outbox is scoped.
        if (services.GetService<IServiceProviderIsService>()?.IsService(typeof(ISparkAfterCommitOutbox)) == true)
            return;

        var durable = services.GetServices<HookRegistration>()
            .Select(r => r.Type)
            .Where(t => typeof(IAfterSaveCommitted).IsAssignableFrom(t) || typeof(IAfterDeleteCommitted).IsAssignableFrom(t))
            .ToList();
        if (durable.Count > 0)
            throw new InvalidOperationException(MissingOutboxMessage(durable));
    }

    public static string MissingOutboxMessage(IEnumerable<Type> hooks)
        => "Durable after-commit hooks are registered (" + string.Join(", ", hooks.Select(t => t.FullName))
           + ") but nothing delivers them: call spark.AddMessaging(). Without it these hooks would never run.";
}
