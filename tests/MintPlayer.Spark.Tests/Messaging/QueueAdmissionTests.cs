using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Services;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// <see cref="QueueAdmission"/> against a fixed clock: the reserved-slot arithmetic, without RavenDB.
/// </summary>
public class QueueAdmissionTests
{
    private static readonly DateTime T0 = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly SparkQueueOptions TwentyPerMinute = new() { MaxPerInterval = 20, Interval = TimeSpan.FromMinutes(1) };

    [Fact]
    public void An_unthrottled_queue_admits_everything_and_reserves_nothing()
    {
        var admission = new QueueAdmission();

        for (var i = 0; i < 100; i++)
            admission.Decide("q", $"m{i}", new SparkQueueOptions(), T0, null).Kind.Should().Be(AdmissionKind.Admit);

        admission.Decide("q", "none", null, T0, null).Kind.Should().Be(AdmissionKind.Admit);
        admission.ReservationCount.Should().Be(0);
    }

    [Fact]
    public void A_full_interval_starts_at_once_and_the_rest_get_one_slot_per_emission_interval()
    {
        var admission = new QueueAdmission();

        var decisions = Enumerable.Range(0, 25)
            .Select(i => admission.Decide("q", $"m{i}", TwentyPerMinute, T0, null))
            .ToList();

        decisions.Take(20).Should().OnlyContain(d => d.Kind == AdmissionKind.Admit);
        decisions.Skip(20).Should().OnlyContain(d => d.Kind == AdmissionKind.Defer);
        decisions.Skip(20).Select(d => d.Slot).Should().Equal(
            T0.AddSeconds(3), T0.AddSeconds(6), T0.AddSeconds(9), T0.AddSeconds(12), T0.AddSeconds(15));
    }

    [Fact]
    public void A_deferred_message_is_admitted_on_its_slot_without_reserving_again()
    {
        var admission = new QueueAdmission();
        for (var i = 0; i < 20; i++)
            admission.Decide("q", $"m{i}", TwentyPerMinute, T0, null);

        var deferred = admission.Decide("q", "late", TwentyPerMinute, T0, null);
        deferred.Kind.Should().Be(AdmissionKind.Defer);

        // Back early (a replayed delivery): the same slot again, still one reservation.
        admission.Decide("q", "late", TwentyPerMinute, T0.AddSeconds(1), null).Should().Be(deferred);
        admission.ReservationCount.Should().Be(1);

        // Back on time (the sweeper wakes it up to FallbackPollInterval late): admitted, slot consumed.
        admission.Decide("q", "late", TwentyPerMinute, deferred.Slot.AddSeconds(20), null).Kind.Should().Be(AdmissionKind.Admit);
        admission.ReservationCount.Should().Be(0);

        // The next newcomer queues behind the slot "late" held, not in front of it.
        admission.Decide("q", "next", TwentyPerMinute, T0, null).Slot.Should().Be(T0.AddSeconds(6));
    }

    [Fact]
    public void A_slot_after_the_expiry_is_refused_without_being_consumed()
    {
        var admission = new QueueAdmission();
        for (var i = 0; i < 20; i++)
            admission.Decide("q", $"m{i}", TwentyPerMinute, T0, null);

        admission.Decide("q", "reset-mail", TwentyPerMinute, T0, expiresAtUtc: T0.AddSeconds(2))
            .Kind.Should().Be(AdmissionKind.Expired);

        admission.ReservationCount.Should().Be(0);
        admission.Decide("q", "other", TwentyPerMinute, T0, null).Slot.Should().Be(T0.AddSeconds(3),
            "the expired message must not have used the slot");
    }

    [Fact]
    public void Queues_are_independent()
    {
        var admission = new QueueAdmission();
        for (var i = 0; i < 20; i++)
            admission.Decide("bulk", $"b{i}", TwentyPerMinute, T0, null);

        admission.Decide("bulk", "b20", TwentyPerMinute, T0, null).Kind.Should().Be(AdmissionKind.Defer);
        admission.Decide("transactional", "t0", TwentyPerMinute, T0, null).Kind.Should().Be(AdmissionKind.Admit);
    }

    [Fact]
    public void A_batch_window_pauses_between_batches()
    {
        var admission = new QueueAdmission();
        var batches = new SparkQueueOptions { BatchSize = 3, MinDelayBetweenBatches = TimeSpan.FromSeconds(10) };

        var slots = Enumerable.Range(0, 7)
            .Select(i => admission.Decide("q", $"m{i}", batches, T0, null))
            .Select(d => d.Kind == AdmissionKind.Admit ? T0 : d.Slot)
            .ToList();

        slots.Should().Equal(T0, T0, T0, T0.AddSeconds(10), T0.AddSeconds(10), T0.AddSeconds(10), T0.AddSeconds(20));
    }

    [Fact]
    public void The_long_run_rate_is_exact()
    {
        var admission = new QueueAdmission();

        // 1000 messages arriving at once: the last starts 980 emission intervals (3 s) after the burst.
        var last = Enumerable.Range(0, 1000)
            .Select(i => admission.Decide("q", $"m{i}", TwentyPerMinute, T0, null))
            .ToList()   // materialized: Range.Select(...).Last() would run the selector for the last element only
            .Last();

        last.Slot.Should().Be(T0.AddSeconds(3 * 980));
    }
}
