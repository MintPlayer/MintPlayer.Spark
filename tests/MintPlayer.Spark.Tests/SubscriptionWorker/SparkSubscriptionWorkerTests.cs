using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.SubscriptionWorker;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions.Documents.Subscriptions;

namespace MintPlayer.Spark.Tests.SubscriptionWorker;

/// <summary>
/// The lifecycle of <see cref="SparkSubscriptionWorker{T}"/> itself, independent of any product
/// worker: graceful stop, create-vs-update, a throwing batch, and the non-recoverable exit. It runs on RavenDB
/// through <see cref="RavenSparkSubscriptions"/>; the store-neutral delivery itself is pinned by
/// <c>SparkSubscriptionsConformanceTests</c> on every provider (#536 D39).
/// <para>
/// Each test runs in its own database (RavenSparkTestDriver), and each creates at most one subscription,
/// so none of them competes with the Community licence cap.
/// </para>
/// </summary>
public class SparkSubscriptionWorkerTests : RavenSparkTestDriver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private sealed class ProbeDoc
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>RavenDB has no schema: its readiness is complete at once (#536 D41).</summary>
    private static readonly ISparkStoreReadiness Ready = new SparkStoreReadiness(Substitute.For<IServiceProvider>());

    private sealed class ProbeWorker(ISparkSubscriptions subscriptions)
        : SparkSubscriptionWorker<ProbeDoc>(subscriptions, NullLoggerFactory.Instance, Ready)
    {
        public const string Name = "Probe";

        /// <summary>Runs for every batch; throw from it to exercise the subscriber-error path.</summary>
        public Func<SparkSubscriptionBatch<ProbeDoc>, Task> OnBatch { get; set; } = _ => Task.CompletedTask;

        public ConcurrentQueue<string> Processed { get; } = new();
        public TaskCompletionSource<Exception> NonRecoverable { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BatchesCompleted;
        public bool Stopped;

        protected override string SubscriptionName => Name;
        protected override TimeSpan RetryDelay => TimeSpan.FromMilliseconds(50);

        protected override async Task ProcessBatchAsync(SparkSubscriptionBatch<ProbeDoc> batch, CancellationToken cancellationToken)
        {
            await OnBatch(batch);
            foreach (var item in batch.Items)
                Processed.Enqueue(item.Id);
        }

        protected override Task OnBatchCompletedAsync(int itemCount)
        {
            Interlocked.Increment(ref BatchesCompleted);
            return Task.CompletedTask;
        }

        protected override Task OnWorkerStoppedAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        protected override async Task OnNonRecoverableErrorAsync(Exception exception)
        {
            await base.OnNonRecoverableErrorAsync(exception);
            NonRecoverable.TrySetResult(exception);
        }
    }

    /// <summary>Starts the worker, runs <paramref name="body"/>, and always stops and disposes it.</summary>
    private static async Task RunAsync(ProbeWorker worker, Func<Task> body)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await body();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private async Task<string> SeedDocAsync(string name)
    {
        var doc = new ProbeDoc { Name = name };
        await SeedAsync(session => session.StoreAsync(doc));
        return doc.Id!;
    }

    private static Task WaitForProcessedAsync(ProbeWorker worker, int count)
        => AsyncWait.UntilAsync(
            () => worker.Processed.Count >= count,
            $"the worker to process {count} document(s)",
            Timeout);

    /// <summary>
    /// The suspected bug: every existing test stops its worker, yet the graceful-shutdown catch was
    /// never covered, which suggested <c>ExecuteAsync</c> leaves through an exception on stop. The
    /// generic host logs a faulted <c>ExecuteTask</c> as "BackgroundService failed".
    /// </summary>
    [Fact]
    public async Task A_graceful_stop_completes_ExecuteTask_without_a_fault()
    {
        var worker = new ProbeWorker(new RavenSparkSubscriptions(Store));
        var id = await SeedDocAsync("one");

        await RunAsync(worker, async () =>
        {
            await WaitForProcessedAsync(worker, 1);
            await worker.StopAsync(CancellationToken.None);
        });

        worker.Processed.Should().Contain(id);
        worker.ExecuteTask.Should().NotBeNull();
        worker.ExecuteTask!.IsCompleted.Should().BeTrue("StopAsync waits for ExecuteAsync to return");
        worker.ExecuteTask.Exception.Should().BeNull(
            "a requested stop is not a failure: {0}", worker.ExecuteTask.Exception?.ToString() ?? "");
        worker.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        worker.Stopped.Should().BeTrue();
    }

    [Fact]
    public async Task An_existing_subscription_is_updated_and_reused()
    {
        // Pre-create it under the same name: the worker's CreateAsync then fails as "already
        // exists", which is the path every restart takes.
        await Store.Subscriptions.CreateAsync(new SubscriptionCreationOptions
        {
            Name = ProbeWorker.Name,
            Query = "from ProbeDocs",
        });
        var worker = new ProbeWorker(new RavenSparkSubscriptions(Store));
        var id = await SeedDocAsync("existing");

        await RunAsync(worker, () => WaitForProcessedAsync(worker, 1));

        worker.Processed.Should().Contain(id);
        (await Store.Subscriptions.GetSubscriptionsAsync(0, 10)).Count.Should().Be(1);
    }

    [Fact]
    public async Task A_batch_that_throws_is_redelivered_after_the_retry_delay()
    {
        var worker = new ProbeWorker(new RavenSparkSubscriptions(Store));
        var attempts = 0;
        worker.OnBatch = _ => Interlocked.Increment(ref attempts) == 1
            ? throw new InvalidOperationException("first delivery fails")
            : Task.CompletedTask;
        var id = await SeedDocAsync("flaky");

        await RunAsync(worker, () => WaitForProcessedAsync(worker, 1));

        attempts.Should().BeGreaterThanOrEqualTo(2, "the failed batch is not acknowledged, so it comes back");
        worker.Processed.Should().Contain(id);
        worker.BatchesCompleted.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Deleting a subscription under a running worker is fatal by design — the worker stops rather
    /// than recreate a definition someone removed on purpose. Isolated: this test's own database
    /// holds nothing but this one subscription.
    /// </summary>
    [Fact]
    public async Task A_subscription_deleted_under_the_worker_is_non_recoverable()
    {
        var worker = new ProbeWorker(new RavenSparkSubscriptions(Store));
        await SeedDocAsync("before");

        Exception? exception = null;
        await RunAsync(worker, async () =>
        {
            await WaitForProcessedAsync(worker, 1);
            await Store.Subscriptions.DeleteAsync(ProbeWorker.Name);

            exception = await worker.NonRecoverable.Task.WaitAsync(Timeout);
            await worker.ExecuteTask!.WaitAsync(Timeout);
        });

        (exception is SubscriptionDoesNotExistException or SubscriptionClosedException).Should().BeTrue(
            "deletion surfaces as one of the two terminal subscription errors, got {0}", exception?.GetType().Name ?? "null");
        worker.Stopped.Should().BeTrue();
    }
}
