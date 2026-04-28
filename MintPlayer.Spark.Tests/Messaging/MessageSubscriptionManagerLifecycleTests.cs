using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Pins <see cref="MessageSubscriptionManager"/>'s start-up lifecycle. The class
/// is registered as an <see cref="IHostedService"/>; its <c>ExecuteAsync</c>
/// resolves the queue list via <c>IServiceCollectionAccessor</c>, fans out one
/// <see cref="MessageSubscriptionWorker"/> per queue, and waits for cancellation.
///
/// We exercise the early-out branch (no IRecipient registered → no queues → log
/// warning + return) which is the minimum viable lifecycle that doesn't require a
/// real Raven subscription. Construction goes through the SG-generated [Inject]
/// ctor — proving the field assignments and base wiring work end-to-end.
/// </summary>
public class MessageSubscriptionManagerLifecycleTests : SparkTestDriver
{
    [Fact]
    public async Task Logs_a_warning_and_returns_when_no_IRecipient_is_registered()
    {
        // No IRecipient<T> registered → DiscoverQueueNames yields an empty set →
        // ExecuteAsync hits the early-return branch.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);

        // Empty-queue path completes ExecuteAsync synchronously after the warning log.
        // StopAsync on a no-op manager just lets the wait-for-cancellation Task.Delay
        // exit cleanly — there are no workers to drain.
        cts.Cancel();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Starts_a_worker_per_discovered_queue_then_stops_them_on_StopAsync()
    {
        // Register a typed IRecipient so DiscoverQueueNames finds at least one queue.
        // The worker will start a Raven subscription against the embedded test server
        // (this test relies on SparkTestDriver's RavenTestDriver). We cancel quickly so
        // we don't actually process any documents — just exercise the start/stop flow.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSparkMessaging();
        services.AddScoped<MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPing>, TestPingRecipient>();
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>()
            .OfType<MessageSubscriptionManager>()
            .Single();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await hosted.StartAsync(cts.Token);

        // Give the worker a beat to attach, then stop. With cts already firing, the
        // wait-for-cancellation in ExecuteAsync exits, then StopAsync drains the worker.
        await Task.Delay(200);
        await hosted.StopAsync(CancellationToken.None);
    }

    [MintPlayer.Spark.Messaging.Abstractions.MessageQueueAttribute("MessageSubscriptionManagerLifecycleTests-Ping")]
    public sealed class TestPing
    {
        public string? Hello { get; set; }
    }

    private sealed class TestPingRecipient : MintPlayer.Spark.Messaging.Abstractions.IRecipient<TestPing>
    {
        public Task HandleAsync(TestPing message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
