using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Files;
using MintPlayer.Spark.Files.Scanning;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SubscriptionWorker;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// #536 D41: a hosted service starts before <c>UseSpark</c> applies a relational schema, so a background service whose
/// first step reads a table waits on <see cref="ISparkStoreReadiness"/>. On a fresh SQL database it found no table
/// otherwise: the subscription worker's registry write failed startup, and the deferred scan's first sweep failed.
/// </summary>
/// <remarks>
/// <c>BackgroundService</c> runs <c>ExecuteAsync</c> on its own task, so "nothing ran yet" right after
/// <c>StartAsync</c> proves nothing. The readiness here signals when it is awaited and holds the service until the test
/// releases it: everything the service did before that point is in the substitutes.
/// </remarks>
public class BackgroundStoreReadinessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class HeldReadiness
    {
        public TaskCompletionSource Awaited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISparkStoreReadiness Readiness { get; } = Substitute.For<ISparkStoreReadiness>();

        // Honours the token, as SparkStoreReadiness does: a host that stops while the schema step runs cancels the wait.
        public HeldReadiness()
            => Readiness.WaitAsync(Arg.Any<CancellationToken>()).Returns(call =>
            {
                Awaited.TrySetResult();
                return Gate.Task.WaitAsync(call.Arg<CancellationToken>());
            });

        public async Task WaitUntilAwaitedAsync()
        {
            var awaited = await Task.WhenAny(Awaited.Task, Task.Delay(Timeout));
            awaited.Should().BeSameAs(Awaited.Task, "the service waits for the store's readiness before its first store call");
        }
    }

    public sealed class ProbeDoc
    {
        public string? Id { get; set; }
    }

    private sealed class GatedWorker(ISparkSubscriptions subscriptions, ISparkStoreReadiness readiness)
        : SparkSubscriptionWorker<ProbeDoc>(subscriptions, NullLoggerFactory.Instance, readiness)
    {
        protected override string SubscriptionName => "Gated";

        protected override Task ProcessBatchAsync(SparkSubscriptionBatch<ProbeDoc> batch, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task A_subscription_worker_writes_its_registry_only_once_the_store_is_ready()
    {
        var subscriptions = Substitute.For<ISparkSubscriptions>();
        var held = new HeldReadiness();
        using var worker = new GatedWorker(subscriptions, held.Readiness);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await held.WaitUntilAwaitedAsync();
            subscriptions.ReceivedCalls().Should().BeEmpty("the schema is not applied yet, so the registry has no table");

            held.Gate.SetResult();
            await worker.ExecuteTask!.WaitAsync(Timeout);
            await subscriptions.Received(1).EnsureAsync(
                Arg.Is<SparkSubscriptionDefinition<ProbeDoc>>(d => d.Name == "Gated"), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_subscription_worker_stopped_before_the_store_is_ready_ends_without_touching_it()
    {
        var subscriptions = Substitute.For<ISparkSubscriptions>();
        var held = new HeldReadiness();
        using var worker = new GatedWorker(subscriptions, held.Readiness);

        await worker.StartAsync(CancellationToken.None);
        await held.WaitUntilAwaitedAsync();
        await worker.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        worker.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion, "a stop during the wait is a clean end, not a fault");
        subscriptions.ReceivedCalls().Should().BeEmpty("the store never became ready");
    }

    [Fact]
    public async Task The_deferred_scan_sweeps_only_once_the_store_is_ready()
    {
        var (service, store, held) = DeferredScan();
        using var _ = service;

        await service.StartAsync(CancellationToken.None);
        try
        {
            await held.WaitUntilAwaitedAsync();
            store.ReceivedCalls().Should().BeEmpty("the schema is not applied yet, so the uploads have no table");

            held.Gate.SetResult();
            await AsyncWait.UntilAsync(() => store.ReceivedCalls().Any(), "the first sweep to open a session", Timeout);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_deferred_scan_stopped_before_the_store_is_ready_ends_without_touching_it()
    {
        var (service, store, held) = DeferredScan();
        using var _ = service;

        await service.StartAsync(CancellationToken.None);
        await held.WaitUntilAwaitedAsync();
        await service.StopAsync(CancellationToken.None).WaitAsync(Timeout);

        service.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion, "a stop during the wait is a clean end, not a fault");
        store.ReceivedCalls().Should().BeEmpty("the store never became ready");
    }

    private static (DeferredFileScanService Service, ISparkStore Store, HeldReadiness Held) DeferredScan()
    {
        var store = Substitute.For<ISparkStore>();
        var fileStore = Substitute.For<IFileStore>();
        var options = Options.Create(new SparkFilesOptions
        {
            ScanMode = SparkFileScanMode.Deferred,
            ScanSweepInterval = TimeSpan.FromHours(1),
        });
        var runner = new FileScanRunner([Substitute.For<IFileScanner>()], fileStore, options, NullLogger<FileScanRunner>.Instance);
        var scanner = new DeferredFileScanner(runner, store, fileStore, Substitute.For<IModelLoader>(), TimeProvider.System,
            NullLogger<DeferredFileScanner>.Instance);
        var held = new HeldReadiness();
        var service = new DeferredFileScanService(scanner, runner, options, TimeProvider.System,
            NullLogger<DeferredFileScanService>.Instance, held.Readiness);
        return (service, store, held);
    }
}
