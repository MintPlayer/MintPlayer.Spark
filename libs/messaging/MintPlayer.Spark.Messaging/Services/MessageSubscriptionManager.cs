using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using System.Reflection;

namespace MintPlayer.Spark.Messaging.Services;

internal sealed partial class MessageSubscriptionManager : BackgroundService
{
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;
    [Inject] private readonly ILogger<MessageSubscriptionManager> logger;
    [Inject] private readonly ILoggerFactory loggerFactory;
    private readonly List<MessageSubscriptionWorker> workers = new();

    private IEnumerable<string> QueueNames => DiscoverQueueNames(serviceProvider);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueNameList = QueueNames.ToList();

        if (queueNameList.Count == 0)
        {
            logger.LogWarning("No message queues discovered from IRecipient<T> registrations. MessageSubscriptionManager will not start any workers.");
            return;
        }

        logger.LogInformation("MessageSubscriptionManager discovered {Count} queue(s): {Queues}", queueNameList.Count, string.Join(", ", queueNameList));

        var workerTasks = new List<Task>();

        foreach (var queueName in queueNameList)
        {
            // Pass the loggerFactory through; MessageSubscriptionWorker forwards it to the
            // base, which scopes the logger to the worker's concrete type via PostConstruct.
            var worker = new MessageSubscriptionWorker(
                queueName,
                documentStore,
                serviceProvider,
                options,
                loggerFactory);

            workers.Add(worker);

            logger.LogInformation("Starting subscription worker for queue '{QueueName}'", queueName);
            workerTasks.Add(worker.StartAsync(stoppingToken));
        }

        await Task.WhenAll(workerTasks);

        // Wait until cancellation is requested, then stop all workers
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("MessageSubscriptionManager stopping, shutting down {Count} worker(s)", workers.Count);

        var stopTasks = workers.Select(w => w.StopAsync(cancellationToken));
        await Task.WhenAll(stopTasks);

        foreach (var worker in workers)
        {
            if (worker is IDisposable disposable)
                disposable.Dispose();
        }

        workers.Clear();

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// A queue is worth a worker exactly when its message type has a recipient, which is the
    /// question <see cref="MessageRecipientRegistry"/> exists to answer — so the scan over the
    /// registered <c>IRecipient&lt;T&gt;</c> descriptors lives there rather than being repeated here.
    /// </summary>
    private static IEnumerable<string> DiscoverQueueNames(IServiceProvider serviceProvider)
    {
        var registry = serviceProvider.GetService<MessageRecipientRegistry>();
        if (registry is null)
            return [];

        // Qualified: this type has its own QueueNames property, which would shadow the helper.
        return registry.ConsumedMessageTypes
            .Select(Services.QueueNames.ForMessageType)
            .ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// Provides access to the IServiceCollection at runtime for queue discovery.
/// </summary>
internal interface IServiceCollectionAccessor
{
    IServiceCollection Services { get; }
}

internal sealed partial class ServiceCollectionAccessor : IServiceCollectionAccessor
{
    [Inject] public IServiceCollection Services { get; }
}
