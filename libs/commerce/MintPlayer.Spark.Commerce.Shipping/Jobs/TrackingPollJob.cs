using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Commerce.Shipping.Jobs;

/// <summary>
/// Polls the tracking of parcels whose carrier pushes no webhooks (<see cref="CarrierCapabilities.TrackingPull"/>
/// without <see cref="CarrierCapabilities.TrackingWebhooks"/>: bpost, PostNL). Idempotent: events are deduplicated.
/// </summary>
internal sealed partial class TrackingPollJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Shipping:TrackingPollSchedule</c> overrides it.</summary>
    public static string CronSchedule => "*/30 * * * *";

    [Inject] private readonly ISparkShipping shipping;

    public Task RunAsync(CancellationToken cancellationToken) => shipping.PollTrackingAsync(cancellationToken);
}
