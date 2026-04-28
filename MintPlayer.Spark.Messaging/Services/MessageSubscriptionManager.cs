using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
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
            var workerLogger = loggerFactory.CreateLogger<MessageSubscriptionWorker>();
            var worker = new MessageSubscriptionWorker(
                queueName,
                documentStore,
                serviceProvider,
                options,
                workerLogger);

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

    private static IEnumerable<string> DiscoverQueueNames(IServiceProvider serviceProvider)
    {
        var queueNames = new HashSet<string>(StringComparer.Ordinal);

        // Get all service descriptors from the root service provider
        // We need to look at the IServiceCollection that was used to build the provider.
        // However, at runtime we can enumerate IRecipient<> by scanning registered service types.
        // A pragmatic approach: scan all assemblies for types implementing IRecipient<T>
        // that are registered in DI.

        // Get the service collection if available (registered by our extension method)
        var serviceDescriptors = serviceProvider.GetService<IServiceCollectionAccessor>()?.Services;
        if (serviceDescriptors == null)
        {
            return queueNames;
        }

        foreach (var descriptor in serviceDescriptors)
        {
            var serviceType = descriptor.ServiceType;
            if (!serviceType.IsGenericType || serviceType.GetGenericTypeDefinition() != typeof(IRecipient<>))
                continue;

            var messageType = serviceType.GetGenericArguments()[0];
            var queueAttribute = messageType.GetCustomAttribute<MessageQueueAttribute>();
            var queueName = queueAttribute?.QueueName ?? messageType.FullName!;
            queueNames.Add(queueName);
        }

        return queueNames;
    }
}

/// <summary>
/// Provides access to the IServiceCollection at runtime for queue discovery.
/// </summary>
internal interface IServiceCollectionAccessor
{
    IServiceCollection Services { get; }
}

internal sealed class ServiceCollectionAccessor : IServiceCollectionAccessor
{
    public IServiceCollection Services { get; }

    public ServiceCollectionAccessor(IServiceCollection services)
    {
        Services = services;
    }
}
