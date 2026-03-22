using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.Exceptions.Documents.Subscriptions;
using Raven.Client.Exceptions.Security;

namespace MintPlayer.Spark.SubscriptionWorker;

/// <summary>
/// Abstract base class for RavenDB subscription workers with built-in retry logic
/// and ASP.NET Core lifecycle management.
/// </summary>
/// <typeparam name="T">The document type to subscribe to.</typeparam>
public abstract class SparkSubscriptionWorker<T> : BackgroundService where T : class
{
    protected IDocumentStore DocumentStore { get; }
    protected ILogger Logger { get; }

    /// <summary>The RavenDB subscription name. Defaults to class name minus common suffixes.</summary>
    protected virtual string SubscriptionName
        => GetType().Name
            .Replace("SubscriptionWorker", "")
            .Replace("Worker", "");

    /// <summary>Optional database override. Null uses the store default.</summary>
    protected virtual string? Database => null;

    /// <summary>Whether to reconnect after normal completion. Default: true.</summary>
    protected virtual bool KeepRunning => true;

    /// <summary>Wait time before connection retry. Default: 30 seconds.</summary>
    protected virtual TimeSpan RetryDelay => TimeSpan.FromSeconds(30);

    /// <summary>Max erroneous period before giving up on connection. Default: 5 minutes.</summary>
    protected virtual TimeSpan MaxDownTime => TimeSpan.FromMinutes(5);

    /// <summary>Maximum documents per subscription batch. Default: 256.</summary>
    protected virtual int MaxDocsPerBatch => 256;

    protected SparkSubscriptionWorker(IDocumentStore store, ILogger logger)
    {
        DocumentStore = store;
        Logger = logger;
    }

    /// <summary>
    /// Configure the subscription query/filter. Called once during startup to create
    /// or update the subscription in RavenDB.
    /// </summary>
    protected abstract SubscriptionCreationOptions ConfigureSubscription();

    /// <summary>
    /// Process a batch of documents delivered by the subscription.
    /// </summary>
    protected abstract Task ProcessBatchAsync(SubscriptionBatch<T> batch, CancellationToken cancellationToken);

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
        await EnsureSubscriptionExistsAsync(stoppingToken);
        await OnWorkerStartedAsync();

        Logger.LogInformation("Subscription worker '{Name}' started", SubscriptionName);

        try
        {
            await RunSubscriptionLoopAsync(stoppingToken);
        }
        finally
        {
            await OnWorkerStoppedAsync();
            Logger.LogInformation("Subscription worker '{Name}' stopped", SubscriptionName);
        }
    }

    private async Task EnsureSubscriptionExistsAsync(CancellationToken cancellationToken)
    {
        var options = ConfigureSubscription();
        options.Name = SubscriptionName;

        try
        {
            await DocumentStore.Subscriptions.CreateAsync(options, Database, cancellationToken);
            Logger.LogInformation("Created subscription '{Name}'", SubscriptionName);
        }
        catch (Exception)
        {
            // Subscription already exists — update it
            try
            {
                await DocumentStore.Subscriptions.UpdateAsync(new SubscriptionUpdateOptions
                {
                    Name = options.Name,
                    Query = options.Query,
                    CreateNew = true,
                }, Database, cancellationToken);
                Logger.LogDebug("Subscription '{Name}' already exists, updated", SubscriptionName);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to update subscription '{Name}', will try to use existing", SubscriptionName);
            }
        }
    }

    private async Task RunSubscriptionLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var workerOptions = new SubscriptionWorkerOptions(SubscriptionName)
            {
                MaxErroneousPeriod = MaxDownTime,
                TimeToWaitBeforeConnectionRetry = RetryDelay,
                MaxDocsPerBatch = MaxDocsPerBatch,
            };

            if (Database != null)
            {
                workerOptions.Strategy = SubscriptionOpeningStrategy.WaitForFree;
            }

            using var subscriptionWorker = DocumentStore.Subscriptions.GetSubscriptionWorker<T>(workerOptions, Database);

            subscriptionWorker.OnSubscriptionConnectionRetry += exception =>
            {
                if (exception is not OperationCanceledException)
                {
                    Logger.LogWarning(exception, "Subscription '{Name}' connection retry", SubscriptionName);
                }
            };

            subscriptionWorker.OnUnexpectedSubscriptionError += exception =>
            {
                if (exception is not OperationCanceledException)
                {
                    Logger.LogWarning(exception, "Subscription '{Name}' unexpected error", SubscriptionName);
                }
            };

            try
            {
                await subscriptionWorker.Run(async batch =>
                {
                    await ProcessBatchAsync(batch, stoppingToken);
                    await OnBatchCompletedAsync(batch.Items.Count);
                }, stoppingToken);

                // Normal completion
                if (KeepRunning)
                {
                    Logger.LogDebug("Subscription '{Name}' completed normally, reconnecting after {Delay}", SubscriptionName, RetryDelay);
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown
                break;
            }
            catch (SubscriptionInUseException ex)
            {
                // Another node holds the subscription — wait longer
                var waitTime = RetryDelay * 2;
                Logger.LogWarning(ex, "Subscription '{Name}' is in use by another node, retrying in {Delay}", SubscriptionName, waitTime);
                await SafeDelayAsync(waitTime, stoppingToken);
            }
            catch (SubscriberErrorException ex)
            {
                Logger.LogWarning(ex, "Subscription '{Name}' subscriber error, retrying in {Delay}", SubscriptionName, RetryDelay);
                await SafeDelayAsync(RetryDelay, stoppingToken);
            }
            catch (SubscriptionClosedException ex)
            {
                Logger.LogError(ex, "Subscription '{Name}' was closed (non-recoverable)", SubscriptionName);
                await OnNonRecoverableErrorAsync(ex);
                break;
            }
            catch (DatabaseDoesNotExistException ex)
            {
                Logger.LogError(ex, "Database for subscription '{Name}' does not exist (non-recoverable)", SubscriptionName);
                await OnNonRecoverableErrorAsync(ex);
                break;
            }
            catch (SubscriptionDoesNotExistException ex)
            {
                Logger.LogError(ex, "Subscription '{Name}' does not exist (non-recoverable)", SubscriptionName);
                await OnNonRecoverableErrorAsync(ex);
                break;
            }
            catch (SubscriptionInvalidStateException ex)
            {
                Logger.LogError(ex, "Subscription '{Name}' is in invalid state (non-recoverable)", SubscriptionName);
                await OnNonRecoverableErrorAsync(ex);
                break;
            }
            catch (AuthorizationException ex)
            {
                Logger.LogError(ex, "Authorization failed for subscription '{Name}' (non-recoverable)", SubscriptionName);
                await OnNonRecoverableErrorAsync(ex);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (KeepRunning)
                {
                    Logger.LogError(ex, "Subscription '{Name}' unexpected error, retrying in {Delay}", SubscriptionName, RetryDelay);
                    await SafeDelayAsync(RetryDelay, stoppingToken);
                }
                else
                {
                    Logger.LogError(ex, "Subscription '{Name}' unexpected error (non-recoverable, KeepRunning=false)", SubscriptionName);
                    await OnNonRecoverableErrorAsync(ex);
                    break;
                }
            }
        }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down during delay — that's fine
        }
    }
}
