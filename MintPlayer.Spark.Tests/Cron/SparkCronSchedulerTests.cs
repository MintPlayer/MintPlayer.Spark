using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Cron;

/// <summary>
/// Exercises <see cref="SparkCronScheduler"/> against an embedded RavenDB: the end-to-end run loop
/// (with a once-per-second schedule), the empty-registry early-out, error isolation when a job
/// throws or has an invalid expression, the concurrent-run path, and the compare-exchange claim
/// used for cluster-wide exactly-once execution.
/// </summary>
public class SparkCronSchedulerTests : SparkTestDriver
{
    private sealed class TestBuilder : ISparkBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }

    private sealed class RunRecorder
    {
        private readonly ConcurrentBag<string> runs = [];
        public void Record(string name) => runs.Add(name);
        public int Count(string name) => runs.Count(r => r == name);
    }

    // Resolved per run from a DI scope; the constructor dependency proves scope resolution works.
    private sealed class EverySecondJob(RunRecorder recorder) : ISparkCronJob
    {
        public static string CronSchedule => "* * * * * *"; // every second (6-field)
        public Task RunAsync(CancellationToken cancellationToken)
        {
            recorder.Record(nameof(EverySecondJob));
            return Task.CompletedTask;
        }
    }

    private sealed class SiblingJob(RunRecorder recorder) : ISparkCronJob
    {
        public static string CronSchedule => "* * * * * *";
        public Task RunAsync(CancellationToken cancellationToken)
        {
            recorder.Record(nameof(SiblingJob));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingJob(RunRecorder recorder) : ISparkCronJob
    {
        public static string CronSchedule => "* * * * * *";
        public Task RunAsync(CancellationToken cancellationToken)
        {
            recorder.Record(nameof(ThrowingJob));
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class ConcurrentJob(RunRecorder recorder) : ISparkCronJob
    {
        public static string CronSchedule => "* * * * * *";
        public static bool AllowConcurrentRuns => true;
        public Task RunAsync(CancellationToken cancellationToken)
        {
            recorder.Record(nameof(ConcurrentJob));
            return Task.CompletedTask;
        }
    }

    private SparkCronScheduler BuildScheduler(RunRecorder recorder, Action<ISparkCronBuilder> configure)
    {
        var builder = new TestBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(recorder);
        builder.Services.AddSingleton<IDocumentStore>(Store);
        builder.AddCron(configure);

        var provider = builder.Services.BuildServiceProvider();
        return provider.GetServices<IHostedService>().OfType<SparkCronScheduler>().Single();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task Runs_a_scheduled_job_resolved_from_a_DI_scope()
    {
        var recorder = new RunRecorder();
        var scheduler = BuildScheduler(recorder, cron => cron.AddJob<EverySecondJob>());

        await scheduler.StartAsync(CancellationToken.None);
        var ran = await WaitUntilAsync(() => recorder.Count(nameof(EverySecondJob)) > 0, TimeSpan.FromSeconds(8));
        await scheduler.StopAsync(CancellationToken.None);

        ran.Should().BeTrue("the every-second job should fire at least once");
    }

    [Fact]
    public async Task Returns_immediately_when_no_jobs_are_registered()
    {
        var scheduler = BuildScheduler(new RunRecorder(), _ => { });

        // Empty registry → ExecuteAsync logs and returns; start/stop must complete cleanly.
        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_throwing_job_is_isolated_and_does_not_stop_the_schedule()
    {
        var recorder = new RunRecorder();
        var scheduler = BuildScheduler(recorder, cron => cron.AddJob<ThrowingJob>());

        await scheduler.StartAsync(CancellationToken.None);
        // It should attempt the run (recorded) and the scheduler should survive the exception,
        // running it again on the next occurrence.
        var ranTwice = await WaitUntilAsync(() => recorder.Count(nameof(ThrowingJob)) >= 2, TimeSpan.FromSeconds(8));
        await scheduler.StopAsync(CancellationToken.None);

        ranTwice.Should().BeTrue("a thrown exception must be caught so the schedule keeps firing");
    }

    [Fact]
    public async Task An_invalid_cron_expression_skips_only_that_job()
    {
        var recorder = new RunRecorder();
        var scheduler = BuildScheduler(recorder, cron => cron
            .AddJob<EverySecondJob>("not-a-cron-expression", "broken") // unparseable → skipped
            .AddJob<SiblingJob>());                                     // valid → keeps running

        await scheduler.StartAsync(CancellationToken.None);
        var siblingRan = await WaitUntilAsync(() => recorder.Count(nameof(SiblingJob)) > 0, TimeSpan.FromSeconds(8));
        await scheduler.StopAsync(CancellationToken.None);

        siblingRan.Should().BeTrue("the valid sibling job must keep running");
        recorder.Count(nameof(EverySecondJob)).Should().Be(0, "the unparseable job must never run");
    }

    [Fact]
    public async Task A_job_that_allows_concurrent_runs_is_dispatched()
    {
        var recorder = new RunRecorder();
        var scheduler = BuildScheduler(recorder, cron => cron.AddJob<ConcurrentJob>());

        await scheduler.StartAsync(CancellationToken.None);
        var ran = await WaitUntilAsync(() => recorder.Count(nameof(ConcurrentJob)) > 0, TimeSpan.FromSeconds(8));
        await scheduler.StopAsync(CancellationToken.None);

        ran.Should().BeTrue();
    }

    [Fact]
    public async Task Claiming_an_occurrence_is_exactly_once_and_monotonic()
    {
        var ct = CancellationToken.None;
        var occ1 = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var occ2 = occ1.AddMinutes(1);

        // First claim of an occurrence wins.
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, "claim-test", occ1, ct))
            .Should().BeTrue();

        // Re-claiming the same occurrence is rejected (already claimed by "this/another node").
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, "claim-test", occ1, ct))
            .Should().BeFalse();

        // A later occurrence advances the claim.
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, "claim-test", occ2, ct))
            .Should().BeTrue();

        // An earlier (stale) occurrence is rejected once a later one is recorded.
        (await SparkCronScheduler.TryClaimOccurrenceAsync(Store, "claim-test", occ1, ct))
            .Should().BeFalse();
    }
}
