using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MintPlayer.Spark.Cron;

/// <summary>Fluent surface for registering cron jobs.</summary>
public interface ISparkCronBuilder
{
    /// <summary>
    /// Registers <typeparamref name="TJob"/> using the schedule it ships via its
    /// <see cref="ISparkCronJob.CronSchedule"/> static abstract member.
    /// </summary>
    ISparkCronBuilder AddJob<TJob>() where TJob : class, ISparkCronJob;

    /// <summary>
    /// Registers <typeparamref name="TJob"/> with a caller-supplied schedule, overriding the job's
    /// shipped default. This lets a consumer run a packaged job on its own schedule, and — by giving
    /// each registration a distinct <paramref name="name"/> — run the same job type on several
    /// schedules.
    /// </summary>
    /// <param name="cronSchedule">The cron expression (NCrontab syntax) to run on.</param>
    /// <param name="name">
    /// Unique name for this registration. Defaults to the job type name. Also used as the
    /// cluster-wide compare-exchange lock key, so it must be unique across all registered jobs.
    /// </param>
    /// <param name="timeZone">
    /// The IANA zone (<c>Europe/Brussels</c>) whose local wall clock the expression is evaluated on.
    /// <see langword="null"/> takes the job's <see cref="ISparkCronJob.TimeZone"/>, which defaults to UTC.
    /// A wall clock DST skips fires at the first valid instant after the gap; one DST repeats fires once.
    /// </param>
    ISparkCronBuilder AddJob<TJob>(string cronSchedule, string? name = null, string? timeZone = null) where TJob : class, ISparkCronJob;
}

internal sealed class SparkCronBuilder(IServiceCollection services, SparkCronJobRegistry registry) : ISparkCronBuilder
{
    public ISparkCronBuilder AddJob<TJob>() where TJob : class, ISparkCronJob
        => AddJob<TJob>(TJob.CronSchedule);              // static abstract default, read here

    public ISparkCronBuilder AddJob<TJob>(string cronSchedule, string? name = null, string? timeZone = null) where TJob : class, ISparkCronJob
    {
        timeZone ??= TJob.TimeZone;
        if (!CronScheduleParser.TryResolveTimeZone(timeZone, out _))
            throw new ArgumentException(
                $"'{timeZone}' is not a time zone this server knows. Use an IANA id such as 'Europe/Brussels', or leave it empty for UTC.",
                nameof(timeZone));

        if (string.IsNullOrWhiteSpace(cronSchedule))
            throw new ArgumentException("Cron schedule must not be null or empty.", nameof(cronSchedule));

        // Fail fast on a malformed expression at registration, rather than letting the scheduler
        // discover it at runtime and silently never run the job. (A syntactically-valid but
        // never-occurring expression still passes here and is handled by the scheduler loop.)
        if (!CronScheduleParser.TryParse(cronSchedule, out _, out _))
            throw new ArgumentException(
                $"'{cronSchedule}' is not a valid NCrontab expression " +
                "(five fields = minute precision, six = seconds).",
                nameof(cronSchedule));

        // Resolved per-run from a DI scope, so [Inject] dependencies work — never `new TJob()`.
        services.TryAddScoped<TJob>();

        registry.Add(new CronJobDescriptor(
            JobType: typeof(TJob),
            Name: name ?? typeof(TJob).Name,
            CronSchedule: cronSchedule,
            AllowConcurrentRuns: TJob.AllowConcurrentRuns,
            TimeZone: string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim()));

        return this;
    }
}
