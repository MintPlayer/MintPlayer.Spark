using System.Linq.Expressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.SubscriptionWorker;

/// <summary>
/// A hosted service that runs one named subscription (<see cref="ISparkSubscriptions"/>, #536 D39) on whichever store
/// the application uses: durable, one runner at a time across hosts, at least once.
/// </summary>
/// <typeparam name="T">The document type to subscribe to.</typeparam>
/// <remarks>
/// A handler that throws gets the same batch again after <see cref="RetryDelay"/>. A subscription that cannot run
/// (deleted under the worker, a missing database, refused authorization) ends the worker through
/// <see cref="OnNonRecoverableErrorAsync"/> without faulting the host. A definition the store refuses at startup (a
/// licence cap) is thrown on purpose: a running application whose queue is silently dead is worse than one that will
/// not start.
/// </remarks>
public abstract partial class SparkSubscriptionWorker<T> : BackgroundService where T : class
{
    [Inject] protected ISparkSubscriptions Subscriptions { get; }

    /// <summary>
    /// Logger instance scoped to the concrete worker subtype. The source generator's <c>[PostConstruct]</c> hook assigns
    /// it once injection completes, so each derived worker logs under its own category.
    /// </summary>
    protected ILogger Logger { get; private set; } = null!;

    [Inject] private readonly ILoggerFactory loggerFactory;

    /// <summary>#536 D41: on a store with a schema, the registry the subscription writes first exists once <c>UseSpark</c> applied it.</summary>
    [Inject] private readonly ISparkStoreReadiness storeReadiness;

    /// <summary>The subscription name. Defaults to class name minus common suffixes.</summary>
    protected virtual string SubscriptionName
        => GetType().Name
            .Replace("SubscriptionWorker", "")
            .Replace("Worker", "");

    /// <summary>Wait time before a failed batch or connection is retried. Default: 30 seconds.</summary>
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(30);

    /// <summary>How long the subscription may keep failing before the worker gives up. Default: 5 minutes.</summary>
    protected virtual TimeSpan MaxDownTime => TimeSpan.FromMinutes(5);

    /// <summary>Maximum documents per batch. Default: 256.</summary>
    protected virtual int MaxDocsPerBatch => 256;

    [PostConstruct]
    private void InitializeLogger() => Logger = loggerFactory.CreateLogger(GetType());

    /// <summary>Which documents the subscription delivers; null for every document of <typeparamref name="T"/>.</summary>
    protected virtual Expression<Func<T, bool>>? Filter => null;

    /// <summary>Process a batch of documents delivered by the subscription. Throw to have it delivered again.</summary>
    protected abstract Task ProcessBatchAsync(SparkSubscriptionBatch<T> batch, CancellationToken cancellationToken);

    /// <summary>Called after the worker starts and the subscription is created.</summary>
    protected virtual Task OnWorkerStartedAsync() => Task.CompletedTask;

    /// <summary>Called when the worker stops (graceful or due to non-recoverable error).</summary>
    protected virtual Task OnWorkerStoppedAsync() => Task.CompletedTask;

    /// <summary>Called after each batch is successfully processed.</summary>
    protected virtual Task OnBatchCompletedAsync(int itemCount) => Task.CompletedTask;

    /// <summary>Called when a non-recoverable error occurs.</summary>
    protected virtual Task OnNonRecoverableErrorAsync(Exception exception) => Task.CompletedTask;

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // #536 D41: the host starts this before UseSpark applies a relational schema; the registry has no table until then.
        try
        {
            await storeReadiness.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var definition = new SparkSubscriptionDefinition<T>(SubscriptionName, Filter);
        // Outside the catch below on purpose: a definition the store refuses fails startup.
        await Subscriptions.EnsureAsync(definition, stoppingToken);
        await OnWorkerStartedAsync();

        Logger.LogInformation("Subscription worker '{Name}' started", SubscriptionName);

        try
        {
            await Subscriptions.RunAsync(definition, new SparkSubscriptionRunOptions
            {
                MaxDocsPerBatch = MaxDocsPerBatch,
                MaxDownTime = MaxDownTime,
                RetryDelay = RetryDelay,
                OnRetry = ex => Logger.LogWarning(ex, "Subscription '{Name}' failed, retrying in {Delay}", SubscriptionName, RetryDelay),
            }, async (batch, cancellationToken) =>
            {
                await ProcessBatchAsync(batch, cancellationToken);
                await OnBatchCompletedAsync(batch.Items.Count);
            }, stoppingToken);
        }
        catch (SparkSubscriptionException ex)
        {
            var cause = ex.InnerException ?? ex;
            Logger.LogError(cause, "Subscription '{Name}' cannot run (non-recoverable)", SubscriptionName);
            await OnNonRecoverableErrorAsync(cause);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await OnWorkerStoppedAsync();
            Logger.LogInformation("Subscription worker '{Name}' stopped", SubscriptionName);
        }
    }
}
