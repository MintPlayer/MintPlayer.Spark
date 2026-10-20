using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Commerce.Inventory.Live;

/// <summary>
/// The engine's commit hook in a host: publishes the availability of the quotas a commit changed (when
/// <c>PublishOnCommit</c>), then tells every <see cref="IInventoryCommitted"/> (scoped, in a scope of its own) what the
/// commit touched. Never throws: the commit already stands.
/// </summary>
internal sealed class InventoryCommitDispatcher(
    InventoryAvailabilityPublisher publisher,
    IServiceScopeFactory scopes,
    IOptions<SparkCommerceInventoryOptions> options,
    ILogger<InventoryCommitDispatcher> logger)
{
    public async Task DispatchAsync(ISparkInventory engine, InventoryCommit commit, CancellationToken cancellationToken)
    {
        if (options.Value.PublishOnCommit && commit.Quotas.Count > 0)
            await publisher.PublishAsync(engine, commit.Quotas, cancellationToken);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            foreach (var handler in scope.ServiceProvider.GetServices<IInventoryCommitted>())
            {
                try
                {
                    await handler.OnCommittedAsync(commit, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{Handler} failed after inventory commit {Operation} of {ClaimIds}.", handler.GetType().Name, commit.Operation, string.Join(", ", commit.ClaimIds));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The inventory commit listeners could not be resolved after {Operation}.", commit.Operation);
        }
    }
}
