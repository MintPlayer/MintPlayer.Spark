using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Wires the messaging pipeline — bus, lane registry, processor, lane pump — against a test store,
/// without a subscription.
/// </summary>
/// <remarks>
/// <para>
/// The pump is driven directly rather than through <c>MessageFeeder</c>. The feeder's only job is to
/// ring a doorbell when a document changes; ringing it from the test removes a subscription (and its
/// delivery latency) from every assertion while leaving the ordering logic — which is what these
/// tests are about — completely intact.
/// </para>
/// <para>
/// One <see cref="TimeProvider"/> serves the whole pipeline, so a test can advance the clock past a
/// retry backoff instead of sleeping through it.
/// </para>
/// </remarks>
internal sealed class MessagingTestHost : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<MessageLanePump> pumps = [];
    private readonly ServiceProvider serviceProvider;
    private readonly LaneRegistry registry;
    private readonly IDocumentStore store;

    public MessagingTestHost(
        IDocumentStore store,
        TimeProvider clock,
        Action<IServiceCollection> registerRecipients,
        Action<ILaneBuilder>? lanes = null,
        SparkMessagingOptions? options = null)
    {
        this.store = store;
        Clock = clock;

        var services = new ServiceCollection();
        registerRecipients(services);
        services.AddSingleton<IServiceCollectionAccessor>(new ServiceCollectionAccessor(services));
        services.AddSingleton<IMessageTypeAllowList, MessageTypeAllowList>();

        // Lanes are declared the way an application declares them — as a registration resolved from
        // the container — so the host exercises the real path rather than a hand-built registry.
        if (lanes is not null)
            services.AddSparkLane(lanes);

        Options = Microsoft.Extensions.Options.Options.Create(options ?? new SparkMessagingOptions());
        services.AddSingleton(Options);

        serviceProvider = services.BuildServiceProvider();
        registry = new LaneRegistry(serviceProvider, Options);
        Bus = new MessageBus(store, Options, clock, new MessageSequence(clock), registry);

        Processor = new MessageProcessor(
            store, serviceProvider, Options, clock, NullLogger<MessageProcessor>.Instance);
    }

    public TimeProvider Clock { get; }
    public IMessageBus Bus { get; }
    public MessageProcessor Processor { get; }
    public IOptions<SparkMessagingOptions> Options { get; }

    /// <summary>Starts a pump for one lane and returns it, so the test can ring its doorbell.</summary>
    public MessageLanePump StartLane(string laneName)
    {
        var pump = new MessageLanePump(
            registry.PlanFor(laneName), store, Processor, Clock, NullLogger.Instance);

        pumps.Add(pump);
        _ = Task.Run(() => pump.RunAsync(cancellation.Token), CancellationToken.None);
        pump.Ring();
        return pump;
    }

    /// <summary>Nudges every started lane — the equivalent of the feeder seeing a write.</summary>
    public void RingAll()
    {
        foreach (var pump in pumps)
            pump.Ring();
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        cancellation.Dispose();
        await serviceProvider.DisposeAsync();
    }
}
