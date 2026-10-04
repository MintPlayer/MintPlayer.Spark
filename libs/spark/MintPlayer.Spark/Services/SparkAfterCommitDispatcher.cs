using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Runs one durable after-commit interceptor for one committed row (#482, D17) — what the outbox message's
/// handler calls, in a fresh DI scope outside any request.
/// </summary>
/// <remarks>
/// The interceptor is looked up by name among the registered interceptors of its phase, then among the Actions
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
            if (Find<IAfterDeleteCommitted>(work) is not { } interceptor)
                return false;
            await interceptor.OnAfterDeleteCommittedAsync(work.Change, cancellationToken);
        }
        else
        {
            if (Find<IAfterSaveCommitted>(work) is not { } interceptor)
                return false;
            await interceptor.OnAfterSaveCommittedAsync(work.Change, cancellationToken);
        }
        return true;
    }

    private TInterceptor? Find<TInterceptor>(SparkAfterCommitWork work) where TInterceptor : class, ISparkInterceptor
    {
        foreach (var interceptor in serviceProvider.GetServices<TInterceptor>())
        {
            if (interceptor.GetType().FullName == work.InterceptorType)
                return interceptor;
        }

        // An Actions class implementing the interceptor for its own type: only for a type of the model.
        if (modelLoader.GetEntityTypeByClrType(work.Change.EntityType) is { } definition
            && typeResolver.Resolve(definition.ClrType) is { } entityType
            && actionsResolver.ResolveForType(entityType) is TInterceptor own
            && own.GetType().FullName == work.InterceptorType)
            return own;

        return null;
    }
}

/// <summary>
/// A durable after-commit interceptor without an outbox is a startup error (#482, D17; owner decision
/// 2026-10-03): the alternative is an interceptor that silently never runs.
/// </summary>
internal static class SparkCommittedInterceptorsStartupCheck
{
    public static void Run(IServiceProvider services)
    {
        // Asked of the container, not resolved: the outbox is scoped.
        if (services.GetService<IServiceProviderIsService>()?.IsService(typeof(ISparkAfterCommitOutbox)) == true)
            return;

        var durable = services.GetServices<InterceptorRegistration>()
            .Select(r => r.Type)
            .Where(t => typeof(IAfterSaveCommitted).IsAssignableFrom(t) || typeof(IAfterDeleteCommitted).IsAssignableFrom(t))
            .ToList();
        if (durable.Count > 0)
            throw new InvalidOperationException(MissingOutboxMessage(durable));
    }

    public static string MissingOutboxMessage(IEnumerable<Type> interceptors)
        => "Durable after-commit interceptors are registered (" + string.Join(", ", interceptors.Select(t => t.FullName))
           + ") but nothing delivers them: call spark.AddMessaging(). Without it these interceptors would never run.";
}
