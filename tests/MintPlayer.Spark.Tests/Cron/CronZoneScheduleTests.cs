using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Cron;

/// <summary>
/// Zone-aware cron schedules (warehouse WX14, Q-W1): the expression is evaluated on the zone's local wall
/// clock; a wall clock DST skips fires at the first valid instant after the gap, and one DST repeats fires
/// once (its first occurrence). Without a zone the schedule is UTC, exactly as before.
/// </summary>
/// <remarks>
/// 2026 transitions used below: Europe/Brussels springs forward 29 March 01:00Z (02:00 CET → 03:00 CEST)
/// and falls back 25 October 01:00Z (03:00 CEST → 02:00 CET); America/New_York springs forward 8 March
/// 07:00Z (02:00 EST → 03:00 EDT) and falls back 1 November 06:00Z (02:00 EDT → 01:00 EST).
/// </remarks>
public class CronZoneScheduleTests
{
    private static DateTime Utc(string iso)
        => DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static TimeZoneInfo Zone(string id)
    {
        CronScheduleParser.TryResolveTimeZone(id, out var zone).Should().BeTrue();
        zone.Should().NotBeNull();
        return zone!;
    }

    /// <summary>The next <paramref name="count"/> occurrences after <paramref name="fromUtc"/>, each from the previous one.</summary>
    private static List<DateTime> Occurrences(string cron, string? zoneId, string fromUtc, int count)
    {
        CronScheduleParser.TryParse(cron, out var schedule, out _).Should().BeTrue();
        var zone = zoneId is null ? null : Zone(zoneId);
        var result = new List<DateTime>();
        var cursor = Utc(fromUtc);
        for (var i = 0; i < count; i++)
        {
            var next = CronScheduleParser.NextOccurrenceUtc(schedule, zone, cursor);
            next.Should().NotBeNull();
            result.Add(next!.Value);
            cursor = next.Value;
        }
        return result;
    }

    // --- Spring-forward gap: the first valid instant after the gap -------------------------------------

    [Fact]
    public void Brussels_a_wall_clock_in_the_spring_gap_fires_at_the_first_valid_instant()
    {
        // 02:30 does not exist on 29 March; the first valid instant is 03:00 CEST = 01:00Z.
        Occurrences("30 2 * * *", "Europe/Brussels", "2026-03-28T12:00:00Z", 2).Should().Equal(
            Utc("2026-03-29T01:00:00Z"),
            Utc("2026-03-30T00:30:00Z")); // 02:30 CEST the next day
    }

    [Fact]
    public void Brussels_several_wall_clocks_in_the_gap_collapse_into_one_run()
    {
        // 02:00, 02:15, 02:30 and 02:45 are all skipped by DST: one run at 03:00 CEST, then 02:00 CEST next day.
        Occurrences("*/15 2 * * *", "Europe/Brussels", "2026-03-29T00:00:00Z", 2).Should().Equal(
            Utc("2026-03-29T01:00:00Z"),
            Utc("2026-03-30T00:00:00Z"));
    }

    [Fact]
    public void New_York_a_wall_clock_in_the_spring_gap_fires_at_the_first_valid_instant()
    {
        // 02:30 does not exist on 8 March; the first valid instant is 03:00 EDT = 07:00Z.
        Occurrences("30 2 * * *", "America/New_York", "2026-03-07T12:00:00Z", 2).Should().Equal(
            Utc("2026-03-08T07:00:00Z"),
            Utc("2026-03-09T06:30:00Z")); // 02:30 EDT the next day
    }

    [Fact]
    public void Six_field_expression_in_the_gap_fires_at_the_transition_itself()
    {
        // 02:30:45 is skipped; the first valid instant is 03:00:00 CEST exactly, not 03:30:45.
        Occurrences("45 30 2 * * *", "Europe/Brussels", "2026-03-28T12:00:00Z", 2).Should().Equal(
            Utc("2026-03-29T01:00:00Z"),
            Utc("2026-03-30T00:30:45Z"));
    }

    [Fact]
    public void A_wall_clock_just_after_the_gap_is_unaffected()
    {
        Occurrences("30 3 * * *", "Europe/Brussels", "2026-03-28T12:00:00Z", 1).Should().Equal(
            Utc("2026-03-29T01:30:00Z")); // 03:30 CEST
    }

    // --- Autumn fold: once, at the first occurrence ---------------------------------------------------

    [Fact]
    public void Brussels_a_wall_clock_in_the_autumn_fold_fires_once_at_its_first_occurrence()
    {
        // 02:30 happens at 00:30Z (CEST) and again at 01:30Z (CET); only the first fires.
        Occurrences("30 2 * * *", "Europe/Brussels", "2026-10-24T12:00:00Z", 2).Should().Equal(
            Utc("2026-10-25T00:30:00Z"),
            Utc("2026-10-26T01:30:00Z")); // 02:30 CET the next day; never 2026-10-25T01:30Z
    }

    [Fact]
    public void New_York_a_wall_clock_in_the_autumn_fold_fires_once_at_its_first_occurrence()
    {
        // 01:30 happens at 05:30Z (EDT) and again at 06:30Z (EST); only the first fires.
        Occurrences("30 1 * * *", "America/New_York", "2026-10-31T12:00:00Z", 2).Should().Equal(
            Utc("2026-11-01T05:30:00Z"),
            Utc("2026-11-02T06:30:00Z")); // 01:30 EST the next day; never 2026-11-01T06:30Z
    }

    [Fact]
    public void Six_field_expression_in_the_fold_fires_once()
    {
        Occurrences("15 30 1 * * *", "America/New_York", "2026-10-31T12:00:00Z", 2).Should().Equal(
            Utc("2026-11-01T05:30:15Z"),
            Utc("2026-11-02T06:30:15Z"));
    }

    [Fact]
    public void An_hourly_schedule_does_not_repeat_the_folded_hour()
    {
        // 00:00 EDT, 01:00 EDT, (01:00 EST is the repeat: skipped), 02:00 EST.
        Occurrences("0 * * * *", "America/New_York", "2026-11-01T03:30:00Z", 3).Should().Equal(
            Utc("2026-11-01T04:00:00Z"),
            Utc("2026-11-01T05:00:00Z"),
            Utc("2026-11-01T07:00:00Z"));
    }

    [Fact]
    public void Starting_inside_the_second_pass_of_a_fold_skips_the_repeated_wall_clocks()
    {
        // 01:10Z is 02:10 CET, the second pass; 02:20 and 02:40 already fired in the first pass (CEST).
        Occurrences("*/20 2 * * *", "Europe/Brussels", "2026-10-25T01:10:00Z", 1).Should().Equal(
            Utc("2026-10-26T01:00:00Z")); // 02:00 CET the next day
    }

    [Fact]
    public void Starting_inside_the_first_pass_of_a_fold_runs_the_rest_of_the_first_pass_only()
    {
        // 00:05Z is 02:05 CEST, the first pass.
        Occurrences("*/20 2 * * *", "Europe/Brussels", "2026-10-25T00:05:00Z", 3).Should().Equal(
            Utc("2026-10-25T00:20:00Z"),
            Utc("2026-10-25T00:40:00Z"),
            Utc("2026-10-26T01:00:00Z"));
    }

    [Fact]
    public void Occurrences_across_a_fold_strictly_increase_so_their_claim_keys_are_unambiguous()
    {
        // The cluster claim stores the occurrence as round-trip ISO UTC and compares ordinally.
        var occurrences = Occurrences("*/30 * * * *", "Europe/Brussels", "2026-10-24T23:00:00Z", 8);
        var keys = occurrences.Select(o => o.ToString("O")).ToList();

        keys.Should().OnlyHaveUniqueItems();
        for (var i = 1; i < keys.Count; i++)
            string.CompareOrdinal(keys[i - 1], keys[i]).Should().BeLessThan(0);
        occurrences.Should().AllSatisfy(o => o.Kind.Should().Be(DateTimeKind.Utc));
    }

    // --- UTC default unchanged ------------------------------------------------------------------------

    [Fact]
    public void Without_a_zone_the_schedule_is_utc_and_ignores_every_dst_transition()
    {
        Occurrences("30 2 * * *", null, "2026-03-29T00:00:00Z", 2).Should().Equal(
            Utc("2026-03-29T02:30:00Z"),
            Utc("2026-03-30T02:30:00Z"));
        Occurrences("30 1 * * *", null, "2026-11-01T00:00:00Z", 1).Should().Equal(
            Utc("2026-11-01T01:30:00Z"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UTC")]
    [InlineData("Etc/UTC")]
    public void No_zone_and_utc_both_take_the_utc_path(string? zoneId)
    {
        CronScheduleParser.TryResolveTimeZone(zoneId, out var zone).Should().BeTrue();
        zone.Should().BeNull();
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("not a zone")]
    public void An_unknown_zone_is_refused(string zoneId)
        => CronScheduleParser.TryResolveTimeZone(zoneId, out _).Should().BeFalse();

    [Fact]
    public void A_never_occurring_expression_still_has_no_occurrence_in_a_zone()
    {
        CronScheduleParser.TryParse("0 0 30 2 *", out var schedule, out _).Should().BeTrue();
        CronScheduleParser.NextOccurrenceUtc(schedule, Zone("Europe/Brussels"), Utc("2026-01-01T00:00:00Z")).Should().BeNull();
    }

    // --- The core facility directly -------------------------------------------------------------------

    [Fact]
    public void Core_NextWallClockOccurrence_returns_null_when_the_schedule_does_not_move_forward()
    {
        var zone = SparkTimeZones.Find("Europe/Brussels")!;
        SparkTimeZones.NextWallClockOccurrence(DateTimeOffset.UnixEpoch, zone, wall => wall).Should().BeNull();
        SparkTimeZones.NextWallClockOccurrence(DateTimeOffset.UnixEpoch, zone, _ => null).Should().BeNull();
    }

    // --- Registration and configuration ---------------------------------------------------------------

    private sealed class TestBuilder : ISparkBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }

    private sealed class UtcJob : ISparkCronJob
    {
        public static string CronSchedule => "0 0 * * *";
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BrusselsJob : ISparkCronJob
    {
        public static string CronSchedule => "0 15 * * 1-5";
        public static string? TimeZone => "Europe/Brussels";
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static SparkCronJobRegistry Resolve(TestBuilder builder)
        => builder.Services.BuildServiceProvider().GetRequiredService<SparkCronJobRegistry>();

    [Fact]
    public void A_job_without_a_zone_registers_as_utc()
    {
        var builder = new TestBuilder();
        builder.AddCron(cron => cron.AddJob<UtcJob>());
        Resolve(builder).Jobs.Single().TimeZone.Should().BeNull();
    }

    [Fact]
    public void A_job_ships_its_zone_and_a_registration_can_override_it()
    {
        var builder = new TestBuilder();
        builder.AddCron(cron => cron
            .AddJob<BrusselsJob>()
            .AddJob<BrusselsJob>("0 17 * * 1-5", "ny", "America/New_York"));

        var jobs = Resolve(builder).Jobs;
        jobs.Single(j => j.Name == nameof(BrusselsJob)).TimeZone.Should().Be("Europe/Brussels");
        jobs.Single(j => j.Name == "ny").TimeZone.Should().Be("America/New_York");
    }

    [Fact]
    public void AddJob_throws_on_an_unknown_zone()
    {
        var builder = new TestBuilder();
        var act = () => builder.AddCron(cron => cron.AddJob<UtcJob>("0 0 * * *", "x", "Mars/Olympus_Mons"));
        act.Should().Throw<ArgumentException>().WithParameterName("timeZone");
    }

    [Fact]
    public void Configuration_overrides_only_the_values_it_sets()
    {
        var options = new SparkCronOptions
        {
            Jobs =
            {
                ["a"] = new SparkCronJobOptions { TimeZone = "America/New_York" },
                ["b"] = new SparkCronJobOptions { Schedule = "*/5 * * * *" },
            },
        };

        var a = options.Apply(new CronJobDescriptor(typeof(UtcJob), "a", "0 0 * * *", false, "Europe/Brussels"));
        a.CronSchedule.Should().Be("0 0 * * *");
        a.TimeZone.Should().Be("America/New_York");

        var b = options.Apply(new CronJobDescriptor(typeof(UtcJob), "b", "0 0 * * *", false, "Europe/Brussels"));
        b.CronSchedule.Should().Be("*/5 * * * *");
        b.TimeZone.Should().Be("Europe/Brussels");

        var untouched = new CronJobDescriptor(typeof(UtcJob), "c", "0 0 * * *", false);
        options.Apply(untouched).Should().Be(untouched);
    }

    [Fact]
    public void Configuration_with_an_unknown_zone_or_bad_expression_fails_validation()
    {
        var builder = new TestBuilder();
        builder.AddCron();
        builder.Services.Configure<SparkCronOptions>(o =>
        {
            o.Jobs["bad-zone"] = new SparkCronJobOptions { TimeZone = "Mars/Olympus_Mons" };
            o.Jobs["bad-cron"] = new SparkCronJobOptions { Schedule = "99 99 * * *" };
        });

        var act = () => builder.Services.BuildServiceProvider().GetRequiredService<IOptions<SparkCronOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Configuration_with_valid_entries_passes_validation()
    {
        var builder = new TestBuilder();
        builder.AddCron();
        builder.Services.Configure<SparkCronOptions>(o =>
            o.Jobs["waves"] = new SparkCronJobOptions { Schedule = "0 15 * * 1-5", TimeZone = "Europe/Brussels" });

        builder.Services.BuildServiceProvider().GetRequiredService<IOptions<SparkCronOptions>>().Value
            .Jobs["waves"].TimeZone.Should().Be("Europe/Brussels");
    }
}

/// <summary>
/// The cluster-wide claim with a zone schedule: occurrences on both sides of a DST fold claim once each,
/// and the fold's repeated wall clock can never be claimed a second time.
/// </summary>
public class CronZoneClaimTests : SparkTestDriver
{
    [Fact]
    public async Task Each_occurrence_across_a_fold_is_claimed_exactly_once()
    {
        // 2025's New York fold (2 November, 06:00Z), in the past so the claims stay inside the skew window.
        CronScheduleParser.TryParse("30 1 * * *", out var schedule, out _).Should().BeTrue();
        CronScheduleParser.TryResolveTimeZone("America/New_York", out var zone).Should().BeTrue();

        var first = CronScheduleParser.NextOccurrenceUtc(schedule, zone, new DateTime(2025, 11, 1, 12, 0, 0, DateTimeKind.Utc))!.Value;
        var second = CronScheduleParser.NextOccurrenceUtc(schedule, zone, first)!.Value;
        first.Should().Be(new DateTime(2025, 11, 2, 5, 30, 0, DateTimeKind.Utc));  // 01:30 EDT
        second.Should().Be(new DateTime(2025, 11, 3, 6, 30, 0, DateTimeKind.Utc)); // 01:30 EST the next day

        const string job = "zone-fold-claim";
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, job, first, CancellationToken.None)).Should().BeTrue();
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, job, first, CancellationToken.None)).Should().BeFalse();

        // The fold's second pass of 01:30 (2025-11-02T06:30Z) is never an occurrence, so the next claim is
        // the next day's, and it claims normally; afterwards neither instant can be claimed again.
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, job, second, CancellationToken.None)).Should().BeTrue();
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, job, second, CancellationToken.None)).Should().BeFalse();
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, job, first, CancellationToken.None)).Should().BeFalse();
    }
}
