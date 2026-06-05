namespace MintPlayer.Spark.Cron;

/// <summary>
/// Holds the set of registered cron jobs. Registered as a singleton; the same instance is
/// mutated at configuration time (via <see cref="ISparkCronBuilder"/>) and read at runtime
/// by the scheduler.
/// </summary>
public sealed class SparkCronJobRegistry
{
    private readonly List<CronJobDescriptor> jobs = [];
    private readonly object gate = new();

    /// <summary>The registered jobs, in registration order. Returns a snapshot, safe to enumerate
    /// while configuration is still adding jobs.</summary>
    public IReadOnlyList<CronJobDescriptor> Jobs
    {
        get { lock (gate) return jobs.ToArray(); }
    }

    internal void Add(CronJobDescriptor descriptor)
    {
        // Registration is single-threaded on the normal DI/host path, but guard the read-then-mutate
        // duplicate-name check so it holds even if an app composes services from multiple threads.
        lock (gate)
        {
            if (jobs.Any(j => j.Name == descriptor.Name))
                throw new InvalidOperationException(
                    $"A cron job named '{descriptor.Name}' is already registered. Job names must be unique — " +
                    $"to register the same job type on more than one schedule, pass a distinct 'name' to AddJob.");

            jobs.Add(descriptor);
        }
    }
}
