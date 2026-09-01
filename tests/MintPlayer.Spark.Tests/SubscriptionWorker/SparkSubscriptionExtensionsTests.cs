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

    [Fact]
    public void AddSparkSubscriptions_without_configuration_registers_nothing_it_does_not_need()
    {
        var services = new ServiceCollection();

        services.AddSparkSubscriptions().Should().BeSameAs(services, "the extension must chain");

        // No Configure call means no options registration — the caller gets defaults from
        // IOptions<T>'s own fallback rather than an empty configuration action.
        services.Should().NotContain(d => d.ServiceType == typeof(IConfigureOptions<SparkSubscriptionOptions>));
    }

    [Fact]
    public void AddSparkSubscriptions_applies_the_configuration_callback()
    {
        var services = new ServiceCollection();
        services.AddOptions();

        var configured = false;
        services.AddSparkSubscriptions(_ => configured = true);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<SparkSubscriptionOptions>>().Value;

        options.Should().NotBeNull();
        configured.Should().BeTrue("the callback has to run when the options are resolved");
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
