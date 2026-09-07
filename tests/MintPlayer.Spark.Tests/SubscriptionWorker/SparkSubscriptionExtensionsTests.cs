using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.SubscriptionWorker;
using MintPlayer.Spark.SubscriptionWorker.Abstractions;

namespace MintPlayer.Spark.Tests.SubscriptionWorker;

/// <summary>
/// Registration for the subscription-worker package.
/// <para>
/// These exist as much to make the package MEASURED as to check the behaviour:
/// <c>MintPlayer.Spark.SubscriptionWorker</c> is a shipping package that no test project referenced,
/// so it appeared in no coverage report at all — not at 0%, but absent, which reads as "fine" in
/// every summary. A worker that is never registered fails by quietly not running, which is the same
/// shape of invisible.
/// </para>
/// </summary>
public class SparkSubscriptionExtensionsTests
{
    private sealed class ProbeWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OtherWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// The two facts that used to live here — that calling it without a callback registers no
    /// options, and that a callback is applied — were deleted along with
    /// <c>SparkSubscriptionOptions</c> itself. That type was empty, so the configuration callback
    /// could not change any behaviour; the tests were asserting that an inert mechanism worked.
    /// </summary>
    [Fact]
    public void AddSparkSubscriptions_chains_and_registers_nothing()
    {
        var services = new ServiceCollection();

        services.AddSparkSubscriptions().Should().BeSameAs(services, "the extension must chain");
        services.Should().BeEmpty("there is no subscription infrastructure to register");
    }

    [Fact]
    public void AddSubscriptionWorker_registers_the_worker_as_a_hosted_service()
    {
        var services = new ServiceCollection();

        services.AddSubscriptionWorker<ProbeWorker>().Should().BeSameAs(services, "the extension must chain");

        var hosted = services.BuildServiceProvider().GetServices<IHostedService>().ToList();
        hosted.Should().ContainSingle().Which.Should().BeOfType<ProbeWorker>();
    }

    [Fact]
    public void Several_workers_all_start()
    {
        // AddHostedService appends rather than replaces, so two workers must both be resolved.
        // If this ever collapsed to one, the lost worker would simply never process its
        // subscription and nothing would throw.
        var services = new ServiceCollection();

        services.AddSubscriptionWorker<ProbeWorker>();
        services.AddSubscriptionWorker<OtherWorker>();

        var hosted = services.BuildServiceProvider().GetServices<IHostedService>().ToList();

        hosted.Should().HaveCount(2);
        hosted.Should().Contain(w => w is ProbeWorker);
        hosted.Should().Contain(w => w is OtherWorker);
    }

    [Fact]
    public void Registering_the_same_worker_twice_still_starts_it_once()
    {
        // AddHostedService de-duplicates (TryAddEnumerable on the implementation type), so a worker
        // registered from two places — a library and an application, say — runs one loop rather
        // than two over the same subscription. Pinned because the alternative failure is silent and
        // expensive: doubled processing, not an error.
        var services = new ServiceCollection();

        services.AddSubscriptionWorker<ProbeWorker>();
        services.AddSubscriptionWorker<ProbeWorker>();

        services.BuildServiceProvider().GetServices<IHostedService>().Should().ContainSingle();
    }
}
