using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// How <c>Spark:Messaging</c> binds — and specifically that a configured backoff schedule replaces the
/// default rather than queueing behind it.
/// <para>
/// Retry policy used to be reachable only through a C# delegate in <c>Program.cs</c>, so an operator
/// could not tune a durable bus per environment without a redeploy. Adding the binding re-opened the
/// trap that produced <b>F14</b> in the replication options: .NET's binder appends to a collection that
/// already has elements instead of replacing it, so a non-empty property initializer would survive
/// binding and stay <i>first</i> — meaning a configured "retry after 100ms" would still wait the
/// default five seconds, silently.
/// </para>
/// </summary>
public class SparkMessagingOptionsBindingTests
{
    private static SparkMessagingOptions Bind(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var options = new SparkMessagingOptions();
        configuration.GetSection("Spark:Messaging").Bind(options);
        return options;
    }

    [Fact]
    public void MaxAttempts_binds_from_configuration()
    {
        // The setting that makes a failing message dead-letter on its first attempt, which is what
        // lets a test observe a terminal state in seconds instead of an hour.
        Bind(("Spark:Messaging:MaxAttempts", "1")).MaxAttempts.Should().Be(1);
    }

    [Fact]
    public void A_configured_backoff_schedule_replaces_the_default_rather_than_queueing_behind_it()
    {
        var options = Bind(
            ("Spark:Messaging:BackoffDelays:0", "00:00:00.100"),
            ("Spark:Messaging:BackoffDelays:1", "00:00:00.200"));

        // The *first* element is what matters: it is the delay before the first retry, so a default
        // left in front of the configured values would swallow the configuration entirely while
        // "contains my values" still passed.
        options.ResolvedBackoffDelays.Should().Equal(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void An_unconfigured_schedule_keeps_the_documented_default()
    {
        // The other half: making a configured value win must not cost apps that configure nothing.
        Bind(("Spark:Messaging:MaxAttempts", "3")).ResolvedBackoffDelays
            .Should().Equal(SparkMessagingOptions.DefaultBackoffDelays);
    }

    [Fact]
    public void Scalars_bind_normally()
    {
        var options = Bind(
            ("Spark:Messaging:FallbackPollInterval", "00:00:02"),
            ("Spark:Messaging:RetentionDays", "1"));

        options.FallbackPollInterval.Should().Be(TimeSpan.FromSeconds(2));
        options.RetentionDays.Should().Be(1);
    }

    [Fact]
    public void Queues_bind_per_name()
    {
        var options = Bind(
            ("Spark:Messaging:Queues:mail-bulk:MaxPerInterval", "20"),
            ("Spark:Messaging:Queues:mail-bulk:Interval", "00:01:00"),
            ("Spark:Messaging:Queues:mail-bulk:MaxConcurrency", "2"),
            ("Spark:Messaging:Queues:mail-transactional:MaxAttempts", "12"),
            ("Spark:Messaging:Queues:mail-transactional:Backoff:0", "00:05:00"));

        options.QueueOptionsFor("mail-bulk")!.MaxPerInterval.Should().Be(20);
        options.QueueOptionsFor("mail-bulk")!.MaxConcurrency.Should().Be(2);
        options.QueueOptionsFor("mail-transactional")!.MaxAttempts.Should().Be(12);
        options.QueueOptionsFor("mail-transactional")!.ResolveBackoff(options.ResolvedBackoffDelays)
            .Should().Equal(TimeSpan.FromMinutes(5));
        options.QueueOptionsFor("unconfigured").Should().BeNull();
    }

    /// <summary>
    /// D14: a queue's thresholds declared in code are defaults, and configuration — appsettings,
    /// environment variables, user secrets — overrides them property by property. Everything else in
    /// <c>Spark:Messaging</c> keeps "code wins".
    /// </summary>
    [Fact]
    public void Configuration_overrides_queue_settings_declared_in_code_property_by_property()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spark:Messaging:MaxAttempts"] = "3",
                ["Spark:Messaging:Queues:mail-bulk:MaxPerInterval"] = "5",
                ["Spark:Messaging:Queues:mail-bulk:Backoff:0"] = "00:00:10",
            })
            .Build();
        var services = new ServiceCollection();
        var builder = new MintPlayer.Spark.SparkBuilder(services, configuration);

        builder.AddMessaging(o =>
        {
            o.MaxAttempts = 7;
            o.Queues["mail-bulk"] = new SparkQueueOptions
            {
                MaxPerInterval = 50,
                Interval = TimeSpan.FromMinutes(2),
                Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)],
            };
        });
        // A library declaring its own queue defaults after the app — still overridable by configuration.
        services.Configure<SparkMessagingOptions>(o => o.Queues["mail-bulk"].MaxPerInterval = 60);

        var resolved = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<SparkMessagingOptions>>().Value;

        resolved.MaxAttempts.Should().Be(7, "outside Queues, code still wins");
        var bulk = resolved.QueueOptionsFor("mail-bulk")!;
        bulk.MaxPerInterval.Should().Be(5, "configuration wins for a queue threshold");
        bulk.Interval.Should().Be(TimeSpan.FromMinutes(2), "an unconfigured property keeps the code default");
        bulk.Backoff.Should().Equal([TimeSpan.FromSeconds(10)], "a configured schedule replaces the code one, not appends to it");
    }
}
