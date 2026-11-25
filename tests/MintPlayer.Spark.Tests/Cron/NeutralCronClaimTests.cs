using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Cron;

/// <summary>
/// #536: the cron claim on the store's atomic values, on the database <c>SPARK_TEST_DATABASE</c> picks. One occurrence
/// is claimed once; a later one replaces it; an earlier one is refused; a poisoned value is reclaimed.
/// </summary>
public class NeutralCronClaimTests : SparkTestDriver
{
    public sealed class CronContext : SparkContext;

    [Fact]
    public async Task An_occurrence_is_claimed_once_and_a_poisoned_claim_is_reclaimed()
    {
        await using var host = new SparkEndpointFactory<CronContext>(Database, [], configureSpark: spark => spark.AddCron());
        var values = host.GetService<ISparkAtomicValues>();
        var job = "neutral-" + Guid.NewGuid().ToString("N");
        var first = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);

        (await SparkCronScheduler.TryClaimOccurrenceAsync(values, job, first, default)).Should().BeTrue();
        (await SparkCronScheduler.TryClaimOccurrenceAsync(values, job, first, default)).Should().BeFalse("already claimed");
        (await SparkCronScheduler.TryClaimOccurrenceAsync(values, job, first.AddMinutes(1), default)).Should().BeTrue();
        (await SparkCronScheduler.TryClaimOccurrenceAsync(values, job, first, default)).Should().BeFalse("older than the claim");

        var current = await values.GetAsync($"cron/{job}");
        (await values.TryPutAsync($"cron/{job}", "not a date", current!.Version)).Should().NotBeNull();
        (await SparkCronScheduler.TryClaimOccurrenceAsync(values, job, first, default)).Should().BeTrue("an unparseable value is reclaimed");
    }
}
