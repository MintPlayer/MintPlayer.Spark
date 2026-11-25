using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Commercial;
using Raven.Client.Exceptions.Database;
using Raven.Client.Exceptions.Documents.Subscriptions;
using Raven.Client.Exceptions.Security;

namespace MintPlayer.Spark.Storage.Raven;

/// <summary>
/// <see cref="ISparkSubscriptions"/> as RavenDB data subscriptions (#536 D39). The filter is the client's typed
/// subscription predicate, which it renders to the subscription's RQL; exclusivity is the server's <c>WaitForFree</c>.
/// </summary>
/// <remarks>
/// Measured before #536 (the subscription worker's spike S2): with <c>WaitForFree</c> a standby blocks in <c>Run</c> with
/// no exception and takes over in about a second after the holder closes, or is killed.
/// </remarks>
[Register(typeof(ISparkSubscriptions), ServiceLifetime.Singleton)]
internal sealed partial class RavenSparkSubscriptions : ISparkSubscriptions
{
    [Inject] private readonly IDocumentStore store;

    public async Task EnsureAsync<T>(SparkSubscriptionDefinition<T> definition, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrEmpty(definition.Name);
        var typed = new SubscriptionCreationOptions<T> { Name = definition.Name, Filter = definition.Filter };
        var query = typed.ToSubscriptionCreationOptions(store.Conventions).Query;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Create, or move an existing definition to this filter: one call either way.
                await store.Subscriptions.UpdateAsync(new SubscriptionUpdateOptions { Name = definition.Name, Query = query, CreateNew = true }, token: cancellationToken);
                return;
            }
            catch (LicenseLimitException ex)
            {
                throw new SparkSubscriptionException(
                    $"Subscription '{definition.Name}' cannot be created: the RavenDB licence refused it ({ex.Message}). Reduce the number of "
                    + "subscriptions or raise the licence limit.", ex);
            }
            catch (Exception ex) when (ex is DatabaseDoesNotExistException or AuthorizationException)
            {
                throw new SparkSubscriptionException($"Subscription '{definition.Name}' cannot be created: {ex.Message}", ex);
            }
            catch (RavenException) when (attempt < 3)
            {
                // Two runners creating the same name at once: one create wins and the other's is refused ("already in use
                // in a subscription with different Id", measured in the D39 conformance suite). The retry updates the winner's.
            }
        }
    }

    public async Task RunAsync<T>(SparkSubscriptionDefinition<T> definition, SparkSubscriptionRunOptions options,
        Func<SparkSubscriptionBatch<T>, CancellationToken, Task> handler, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        await EnsureAsync(definition, cancellationToken);

        DateTimeOffset? failingSince = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var worker = store.Subscriptions.GetSubscriptionWorker<T>(new SubscriptionWorkerOptions(definition.Name)
            {
                Strategy = SubscriptionOpeningStrategy.WaitForFree,
                MaxDocsPerBatch = Math.Max(1, options.MaxDocsPerBatch),
                MaxErroneousPeriod = options.MaxDownTime,
                TimeToWaitBeforeConnectionRetry = options.RetryDelay,
            });
            worker.OnSubscriptionConnectionRetry += ex =>
            {
                if (ex is not OperationCanceledException)
                    options.OnRetry?.Invoke(ex);
            };

            try
            {
                await worker.Run(async batch =>
                {
                    var items = batch.Items
                        .Where(i => i.Result is not null)
                        .Select(i => new SparkSubscriptionItem<T>(i.Id, i.Result, i.ChangeVector))
                        .ToList();
                    await handler(new SparkSubscriptionBatch<T>(items), cancellationToken);
                    failingSince = null;
                }, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // A stop tears the connection down under the batch read; whatever Run throws then is the teardown.
                return;
            }
            catch (Exception ex) when (ex is SubscriptionClosedException or SubscriptionDoesNotExistException or SubscriptionInvalidStateException
                or DatabaseDoesNotExistException or AuthorizationException or LicenseLimitException)
            {
                throw new SparkSubscriptionException($"Subscription '{definition.Name}' cannot run: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                // A handler that threw, or a connection that failed past the client's own retries: the batch comes again.
                options.OnRetry?.Invoke(ex);
                failingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - failingSince > options.MaxDownTime)
                    throw new SparkSubscriptionException($"Subscription '{definition.Name}' kept failing for longer than {options.MaxDownTime}.", ex);
            }

            try
            {
                await Task.Delay(options.RetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetNamesAsync(CancellationToken cancellationToken = default)
    {
        var names = new List<string>();
        for (var start = 0; ; start += 128)
        {
            var page = await store.Subscriptions.GetSubscriptionsAsync(start, 128, token: cancellationToken);
            names.AddRange(page.Select(s => s.SubscriptionName));
            if (page.Count < 128)
                return names;
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        try
        {
            await store.Subscriptions.DeleteAsync(name, token: cancellationToken);
        }
        catch (SubscriptionDoesNotExistException)
        {
        }
    }
}
