using Microsoft.Extensions.Configuration;
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
}
