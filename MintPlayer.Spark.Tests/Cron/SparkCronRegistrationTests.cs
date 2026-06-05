using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Tests.Cron;

/// <summary>
/// Covers the cron registration surface: <see cref="SparkCronExtensions.AddCron"/> idempotency,
/// the <c>AddJob</c> overloads (default schedule, override, optional name, multi-schedule), and
/// the registry's duplicate-name guard. No RavenDB needed — pure DI/registry wiring.
/// </summary>
public class SparkCronRegistrationTests
{
    private sealed class TestBuilder : ISparkBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }

    private sealed class JobA : ISparkCronJob
    {
        public static string CronSchedule => "0 0 * * *";
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class JobB : ISparkCronJob
    {
        public static string CronSchedule => "0 1 * * *";
        public static bool AllowConcurrentRuns => true;
        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void AddCron_registers_the_registry_and_scheduler_once_even_across_multiple_calls()
    {
        var builder = new TestBuilder();

        builder.AddCron();
        builder.AddCron(); // second call must reuse the registry and not re-add the hosted service

        var registryDescriptors = builder.Services
            .Where(d => d.ServiceType == typeof(SparkCronJobRegistry))
            .ToList();
        var schedulerDescriptors = builder.Services
            .Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType == typeof(SparkCronScheduler))
            .ToList();

        registryDescriptors.Should().ContainSingle();
        schedulerDescriptors.Should().ContainSingle();
    }

    [Fact]
    public void AddJob_default_overload_captures_the_jobs_shipped_schedule_and_concurrency_flag()
    {
        var builder = new TestBuilder();
        builder.AddCron(cron => cron.AddJob<JobA>().AddJob<JobB>());

        var registry = Resolve(builder);

        registry.Jobs.Should().HaveCount(2);

        var a = registry.Jobs.Single(j => j.Name == nameof(JobA));
        a.CronSchedule.Should().Be("0 0 * * *");
        a.AllowConcurrentRuns.Should().BeFalse();
        a.JobType.Should().Be<JobA>();

        var b = registry.Jobs.Single(j => j.Name == nameof(JobB));
        b.CronSchedule.Should().Be("0 1 * * *");
        b.AllowConcurrentRuns.Should().BeTrue(); // static virtual override
    }

    [Fact]
    public void AddJob_registers_the_job_as_a_scoped_service()
    {
        var builder = new TestBuilder();
        builder.AddCron(cron => cron.AddJob<JobA>());

        var descriptor = builder.Services.Single(d => d.ServiceType == typeof(JobA));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddJob_override_replaces_the_schedule_but_keeps_the_default_name()
    {
        var builder = new TestBuilder();
        builder.AddCron(cron => cron.AddJob<JobA>("*/5 * * * *"));

        var job = Resolve(builder).Jobs.Single();
        job.Name.Should().Be(nameof(JobA));
        job.CronSchedule.Should().Be("*/5 * * * *");
    }

    [Fact]
    public void AddJob_with_name_allows_the_same_type_on_multiple_schedules()
    {
        var builder = new TestBuilder();
        builder.AddCron(cron => cron
            .AddJob<JobA>("0 8 * * *", "a-am")
            .AddJob<JobA>("0 18 * * *", "a-pm"));

        var registry = Resolve(builder);
        registry.Jobs.Select(j => j.Name).Should().BeEquivalentTo(["a-am", "a-pm"]);
        registry.Jobs.Select(j => j.CronSchedule).Should().BeEquivalentTo(["0 8 * * *", "0 18 * * *"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AddJob_throws_when_the_schedule_is_missing(string? schedule)
    {
        var builder = new TestBuilder();

        var act = () => builder.AddCron(cron => cron.AddJob<JobA>(schedule!));

        act.Should().Throw<ArgumentException>().WithParameterName("cronSchedule");
    }

    [Theory]
    [InlineData("not-a-cron-expression")] // single token, unparseable
    [InlineData("99 99 * * *")]           // out-of-range fields
    [InlineData("* * *")]                 // too few fields
    [InlineData("* * * * * * *")]         // too many fields
    public void AddJob_throws_when_the_schedule_is_unparseable(string schedule)
    {
        var builder = new TestBuilder();

        // Fail fast at registration rather than letting the scheduler silently never run the job.
        var act = () => builder.AddCron(cron => cron.AddJob<JobA>(schedule, "x"));

        act.Should().Throw<ArgumentException>().WithParameterName("cronSchedule");
    }

    [Fact]
    public void AddJob_accepts_a_valid_but_never_occurring_expression()
    {
        var builder = new TestBuilder();

        // "30 February" is syntactically valid (so registration succeeds); the scheduler detects the
        // no-future-occurrence case at runtime and disables only that loop.
        var act = () => builder.AddCron(cron => cron.AddJob<JobA>("0 0 30 2 *", "feb30"));

        act.Should().NotThrow();
        Resolve(builder).Jobs.Single().CronSchedule.Should().Be("0 0 30 2 *");
    }

    [Fact]
    public void AddJob_throws_when_the_same_name_is_registered_twice()
    {
        var builder = new TestBuilder();

        var act = () => builder.AddCron(cron => cron
            .AddJob<JobA>()
            .AddJob<JobA>()); // same default name → collision

        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    private static SparkCronJobRegistry Resolve(ISparkBuilder builder)
        => builder.Services.BuildServiceProvider().GetRequiredService<SparkCronJobRegistry>();
}
