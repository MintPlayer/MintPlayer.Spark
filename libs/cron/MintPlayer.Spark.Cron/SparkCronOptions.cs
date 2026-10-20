using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Cron;

/// <summary>
/// Configuration for registered cron jobs, bound from <c>Spark:Cron</c>. Lets an operator move a job to
/// another schedule or another time zone without a code change:
/// <code>
/// "Spark": { "Cron": { "Jobs": {
///   "Warehouse.Waves.Ghent": { "Schedule": "0 15 * * 1-5", "TimeZone": "Europe/Brussels" }
/// } } }
/// </code>
/// Keys are registration names (<see cref="CronJobDescriptor.Name"/>). An entry only overrides the values
/// it sets. Validated when the host starts: an invalid expression or an unknown zone fails startup.
/// </summary>
public sealed class SparkCronOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Cron";

    /// <summary>Per-job overrides, keyed by registration name.</summary>
    public Dictionary<string, SparkCronJobOptions> Jobs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The registration with this configuration's override for it applied (unchanged without one).</summary>
    internal CronJobDescriptor Apply(CronJobDescriptor descriptor)
    {
        if (!Jobs.TryGetValue(descriptor.Name, out var entry) || entry is null)
            return descriptor;
        return descriptor with
        {
            CronSchedule = string.IsNullOrWhiteSpace(entry.Schedule) ? descriptor.CronSchedule : entry.Schedule.Trim(),
            TimeZone = string.IsNullOrWhiteSpace(entry.TimeZone) ? descriptor.TimeZone : entry.TimeZone.Trim(),
        };
    }
}

/// <summary>An override for one registered cron job.</summary>
public sealed class SparkCronJobOptions
{
    /// <summary>The cron expression (NCrontab; five fields = minutes, six = seconds), replacing the registered one.</summary>
    public string? Schedule { get; set; }

    /// <summary>
    /// The IANA zone (<c>Europe/Brussels</c>) the schedule is evaluated in, replacing the registered one.
    /// <c>"UTC"</c> forces UTC; leaving it unset keeps the registered zone.
    /// </summary>
    public string? TimeZone { get; set; }
}

/// <summary>
/// Fails startup on a <c>Spark:Cron:Jobs</c> entry with an invalid expression or an unknown zone, or one whose key names
/// no registered job (a typo there would otherwise leave the job on its registered schedule without a word).
/// </summary>
internal sealed class SparkCronOptionsValidator(SparkCronJobRegistry? registry = null) : IValidateOptions<SparkCronOptions>
{
    public ValidateOptionsResult Validate(string? name, SparkCronOptions options)
    {
        var failures = new List<string>();
        var registered = registry?.Jobs.Select(j => j.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (job, entry) in options.Jobs)
        {
            if (registered is not null && !registered.Contains(job))
                failures.Add($"Spark:Cron:Jobs:{job} names no registered cron job (names are case-sensitive; registered: {string.Join(", ", registered.Order(StringComparer.Ordinal))}).");
            if (entry is null)
                continue;
            if (!string.IsNullOrWhiteSpace(entry.Schedule) && !CronScheduleParser.TryParse(entry.Schedule, out _, out _))
                failures.Add($"Spark:Cron:Jobs:{job}:Schedule '{entry.Schedule}' is not a valid NCrontab expression (five fields = minutes, six = seconds).");
            if (!CronScheduleParser.TryResolveTimeZone(entry.TimeZone, out _))
                failures.Add($"Spark:Cron:Jobs:{job}:TimeZone '{entry.TimeZone}' is not a time zone this server knows (use an IANA id such as 'Europe/Brussels').");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
