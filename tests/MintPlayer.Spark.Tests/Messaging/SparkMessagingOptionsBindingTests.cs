using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// How <c>Spark:Messaging</c> binds, and why a retry ladder is a string rather than an array.
/// </summary>
/// <remarks>
/// <para>
/// The well-known trap — the one that produced <b>F14</b> in the replication options — is that .NET's
/// binder <i>appends</i> to a collection that already has elements rather than replacing it, so a
/// non-empty property initializer survives binding and stays first.
/// </para>
/// <para>
/// Measuring it turned up two more, and the second needs no initializer at all: <b>two configuration
/// layers overlay element-wise</b>, so a base <c>[1m,5m,1h]</c> overridden by <c>[7s]</c> binds to
/// <c>[7s,5m,1h]</c> — a ladder nobody wrote, assembled from two files that each look correct; and
/// <b>re-binding the same options object doubles the array</b>, which <c>Configure</c> plus
/// <c>PostConfigure</c>, or an <c>IOptionsMonitor</c> reload, will do.
/// </para>
/// <para>
/// A scalar string is immune to all three, and these tests pin that rather than describing it.
/// </para>
/// </remarks>
public class SparkMessagingOptionsBindingTests
{
    private static IConfigurationRoot Configuration(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static SparkMessagingOptions Bind(params (string Key, string Value)[] settings)
    {
        var options = new SparkMessagingOptions();
        Configuration(settings).GetSection("Spark:Messaging").Bind(options);
        return options;
    }

    [Fact]
    public void A_configured_ladder_replaces_the_default_rather_than_queueing_behind_it()
    {
        var options = Bind(("Spark:Messaging:DefaultRetry", "100ms 200ms"));

        // The FIRST rung is what matters: a default left in front would swallow the configuration
        // entirely while a "contains my values" assertion still passed.
        options.DefaultRetry.Should().Be("100ms 200ms");
    }

    [Fact]
    public void A_later_configuration_layer_replaces_the_whole_ladder()
    {
        // The trap an array falls into. As two JSON files: base declares three rungs, the environment
        // override declares one. With TimeSpan[] the result is [7s, 5m, 1h]. With a scalar it is 7s.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Spark:Messaging:DefaultRetry", "1m 5m 1h")])
            .AddInMemoryCollection([new("Spark:Messaging:DefaultRetry", "7s")])
            .Build();

        var options = new SparkMessagingOptions();
        configuration.GetSection("Spark:Messaging").Bind(options);

        options.DefaultRetry.Should().Be("7s", "a shorter override must not inherit the tail of the base");
    }

    [Fact]
    public void Binding_twice_is_idempotent()
    {
        // Configure + PostConfigure, or an IOptionsMonitor reload, binds the same object more than
        // once. An array would double; a scalar cannot.
        var section = Configuration(("Spark:Messaging:DefaultRetry", "1m 5m")).GetSection("Spark:Messaging");

        var options = new SparkMessagingOptions();
        section.Bind(options);
        section.Bind(options);

        options.DefaultRetry.Should().Be("1m 5m");
    }

    [Fact]
    public void The_global_override_wins_over_the_default_and_reaches_every_lane()
    {
        // The one switch a test environment sets. It replaces the delay function only — attempt
        // counts still come from each lane, so a test exercises the real dead-letter path.
        var options = Bind(
            ("Spark:Messaging:DefaultRetry", "1m 5m 1h"),
            ("Spark:Messaging:RetryOverride", "5s"));

        options.ResolvedDefaultRetry.Next(1).Should().BeOfType<RetryDecision.RetryAfter>()
            .Which.Delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void An_unconfigured_schedule_keeps_the_documented_default()
    {
        var options = Bind(("Spark:Messaging:RetentionDays", "3"));

        options.DefaultRetry.Should().Be("5s 30s 2m");
        options.ResolvedDefaultRetry.Next(1).Should().BeOfType<RetryDecision.RetryAfter>()
            .Which.Delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Scalars_bind_normally()
    {
        var options = Bind(
            ("Spark:Messaging:ProcessingLease", "00:05:00"),
            ("Spark:Messaging:RetentionDays", "1"));

        options.ProcessingLease.Should().Be(TimeSpan.FromMinutes(5));
        options.RetentionDays.Should().Be(1);
    }
}
