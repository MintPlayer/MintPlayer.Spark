using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Messaging.Services;

internal sealed class MessageSubscriptionManager : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IDocumentStore _documentStore;
    private readonly IOptions<SparkMessagingOptions> _options;
    private readonly ILogger<MessageSubscriptionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEnumerable<string> _queueNames;
    private readonly List<MessageSubscriptionWorker> _workers = new();

    public MessageSubscriptionManager(
        IServiceProvider serviceProvider,
        IDocumentStore documentStore,
        IOptions<SparkMessagingOptions> options,
        ILogger<MessageSubscriptionManager> logger,
        ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _documentStore = documentStore;
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _queueNames = DiscoverQueueNames(serviceProvider);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueNameList = _queueNames.ToList();

        if (queueNameList.Count == 0)
        {
            _logger.LogWarning("No message queues discovered from IRecipient<T> registrations. MessageSubscriptionManager will not start any workers.");
            return;
        }

        _logger.LogInformation("MessageSubscriptionManager discovered {Count} queue(s): {Queues}", queueNameList.Count, string.Join(", ", queueNameList));

        var workerTasks = new List<Task>();

        foreach (var queueName in queueNameList)
        {
            var workerLogger = _loggerFactory.CreateLogger<MessageSubscriptionWorker>();
            var worker = new MessageSubscriptionWorker(
                queueName,
                _documentStore,
                _serviceProvider,
                _options,
                workerLogger);

            _workers.Add(worker);

            _logger.LogInformation("Starting subscription worker for queue '{QueueName}'", queueName);
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
        _logger.LogInformation("MessageSubscriptionManager stopping, shutting down {Count} worker(s)", _workers.Count);

        var stopTasks = _workers.Select(w => w.StopAsync(cancellationToken));
        await Task.WhenAll(stopTasks);

        foreach (var worker in _workers)
        {
            if (worker is IDisposable disposable)
                disposable.Dispose();
        }

        _workers.Clear();

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
