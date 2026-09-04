using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Services;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// Lane declaration: what is rejected at startup, and what the defaults are.
/// </summary>
/// <remarks>
/// These run without a server, because every rule here is about configuration rather than delivery.
/// Each rejection exists because its alternative is a <i>silent</i> fault — misordering, or a lane
/// that is unavailable for days — and silence is what this whole design set out to remove.
/// </remarks>
public class LaneRegistryTests
{
    private record Ordered(string Key);
    private record Other(string Key);

    /// <summary>
    /// Builds a registry the way the framework does — through the container — so these tests exercise
    /// the real registration path rather than a hand-assembled object.
    /// </summary>
    private static LaneRegistry Registry(Action<ILaneBuilder> declare, SparkMessagingOptions? options = null)
        => Build(services => services.AddSparkLane(declare), options);

    private static LaneRegistry Build(Action<IServiceCollection> register, SparkMessagingOptions? options = null)
    {
        var services = new ServiceCollection();
        register(services);

        var resolved = Options.Create(options ?? new SparkMessagingOptions());
        services.AddSingleton(resolved);

        return new LaneRegistry(services.BuildServiceProvider(), resolved);
    }

    [Fact]
    public void An_ordered_lane_missing_a_partition_selector_fails_startup_naming_the_type()
    {
        // Without a key the lane cannot know which messages depend on each other. The alternative to
        // failing here is silent misordering, noticed only as corrupted downstream data.
        // Declared through the typed overload, which is how an application declares a lane: the lane
        // name then comes from the message type itself and cannot drift away from it.
        var registry = Registry(lanes => lanes.Queue<Ordered>().Ordered());

        var act = () => registry.Validate([typeof(Ordered)]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*declares no partition key*");
    }

    [Fact]
    public void Declaring_the_same_lane_twice_fails()
    {
        // Two declarations means two owners for one lane's delivery mode and schedule, and whichever
        // ran last would win silently.
        //
        // Note WHERE this surfaces. Declarations are resolved from the container on first use, not
        // while services are being registered — that is what lets a lane be configured from anything
        // the container holds. So the conflict is raised the first time lanes are needed, which in an
        // application is the startup validation pass, not the AddLane call. Still at startup, still
        // loud; just not at the line that registered it.
        var registry = Registry(lanes =>
        {
            lanes.Queue("orders").Concurrent(1);
            lanes.Queue("orders").Concurrent(4);
        });

        var act = () => registry.Validate([]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*declared twice*");
    }

    [Fact]
    public void An_ordered_lane_whose_ladder_outlasts_the_ceiling_fails_startup()
    {
        // Under Ordered, a failing head blocks its partition until it succeeds or dead-letters, so
        // the schedule's total IS that partition's worst-case downtime. An 11-day ladder on an
        // ordered lane is an outage with a schedule, and it should be refused before it happens
        // rather than discovered eleven days in.
        var registry = Registry(lanes => lanes.Queue("email")
            .Ordered()
            .PartitionBy<Ordered>(m => m.Key)
            .Retry(RetrySchedule.Ladder("1m 5m 1h 6h 1d 3d 7d")));

        var act = () => registry.Validate([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*block one partition*");
    }

    [Fact]
    public void A_long_ladder_is_allowed_when_the_lane_says_the_wait_is_intended()
    {
        var registry = Registry(lanes => lanes.Queue("email")
            .Ordered()
            .PartitionBy<Ordered>(m => m.Key)
            .Retry(RetrySchedule.Ladder("1m 5m 1h"))
            .AcceptPartitionBlock(TimeSpan.FromHours(2)));

        var act = () => registry.Validate([]);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_concurrent_lane_may_carry_any_ladder_because_nothing_waits_behind_it()
    {
        // The ceiling is about partitions blocking, and a concurrent lane has none.
        var registry = Registry(lanes => lanes.Queue("email")
            .Concurrent(8)
            .Retry(RetrySchedule.Ladder("1m 5m 1h 6h 1d 3d 7d")));

        var act = () => registry.Validate([]);

        act.Should().NotThrow();
    }

    [Fact]
    public void An_undeclared_lane_is_concurrent_never_ordered()
    {
        // A silently-ordered lane with no partition key would put every message into one partition
        // and serialize the whole lane — the exact failure partitioning exists to prevent. So the
        // default is the mode that cannot be silently wrong.
        var plan = Build(_ => { }).PlanFor("never-declared");

        plan.Ordered.Should().BeFalse();
        plan.MaxInFlight.Should().Be(1);
    }

    [Fact]
    public void The_global_override_replaces_a_lane_s_own_schedule()
    {
        var registry = Registry(
            lanes => lanes.Queue("orders").Concurrent(1).Retry(RetrySchedule.Ladder("1h 6h")),
            new SparkMessagingOptions { RetryOverride = "5s" });

        var plan = registry.PlanFor("orders");

        plan.Retry.Next(1).Should().BeOfType<RetryDecision.RetryAfter>()
            .Which.Delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_partition_selector_that_returns_an_empty_key_fails_loudly()
    {
        // An empty key would collapse the lane into a single partition and serialize everything,
        // which looks like a performance problem rather than a configuration mistake.
        var registry = Registry(lanes => lanes.Queue("orders")
            .Ordered()
            .PartitionBy<Ordered>(m => m.Key));

        var act = () => registry.PartitionKeyFor("orders", typeof(Ordered), new Ordered(string.Empty));

        act.Should().Throw<InvalidOperationException>().WithMessage("*empty key*");
    }

    [Fact]
    public void A_ladder_derives_its_attempt_limit_so_the_last_rung_is_reachable()
    {
        // The defect this makes unrepresentable: five rungs with MaxAttempts = 5 dead-lettered on the
        // fifth failure, so the fifth rung could never be reached and the declared schedule was a
        // lie. The ladder IS the schedule now.
        var schedule = RetrySchedule.Ladder("1s 2s 3s");

        schedule.Next(1).Should().BeOfType<RetryDecision.RetryAfter>().Which.Delay.Should().Be(TimeSpan.FromSeconds(1));
        schedule.Next(3).Should().BeOfType<RetryDecision.RetryAfter>().Which.Delay.Should().Be(TimeSpan.FromSeconds(3));
        schedule.Next(4).Should().BeOfType<RetryDecision.DeadLetter>();
    }
}
