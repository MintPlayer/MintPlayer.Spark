namespace MintPlayer.Spark.Cron;

/// <summary>
/// Holds the set of registered cron jobs. Registered as a singleton; the same instance is
/// mutated at configuration time (via <see cref="ISparkCronBuilder"/>) and read at runtime
/// by the scheduler.
/// </summary>
public sealed class SparkCronJobRegistry
{
    private readonly List<CronJobDescriptor> jobs = [];

    /// <summary>The registered jobs, in registration order.</summary>
    public IReadOnlyList<CronJobDescriptor> Jobs => jobs;

    internal void Add(CronJobDescriptor descriptor)
    {
        if (jobs.Any(j => j.Name == descriptor.Name))
            throw new InvalidOperationException(
                $"A cron job named '{descriptor.Name}' is already registered. Job names must be unique — " +
                $"to register the same job type on more than one schedule, pass a distinct 'name' to AddJob.");

        jobs.Add(descriptor);
    }
}
