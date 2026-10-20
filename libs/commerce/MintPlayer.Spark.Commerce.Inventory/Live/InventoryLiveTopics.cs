using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Live;

namespace MintPlayer.Spark.Commerce.Inventory.Live;

/// <summary>
/// What a quota's live topic carries (X2): how many units are left and whether it sold out. Deliberately not the
/// capacity or the sold count, which a seller may not want public: a topic payload is public by construction.
/// </summary>
/// <param name="QuotaId">The quota.</param>
/// <param name="Available">Units left for sale now (capacity minus sold minus live holds).</param>
/// <param name="SoldOut">Whether nothing is left.</param>
public sealed record InventoryAvailabilityFrame(string QuotaId, int Available, bool SoldOut);

/// <summary>
/// The live availability of quotas, per quota, on the public topic <c>commerce-inventory:{quotaId}</c>. Ticketing
/// publishes a date or a block as a quota of its own, so "per date, block and quota" (§4.3) are all quotas here.
/// </summary>
/// <remarks>
/// In-process, like every topic: a value published on one instance reaches that instance's sockets. The sweeper runs
/// on one node at a time (the cron lock), so on a multi-instance host the other instances see changes made elsewhere
/// only through their own commits.
/// </remarks>
public sealed class InventoryAvailabilityPublisher(ISparkLiveTopics topics, ILogger<InventoryAvailabilityPublisher> logger)
{
    public const string TopicPrefix = "commerce-inventory:";

    public static string Topic(string quotaId) => TopicPrefix + quotaId;

    /// <summary>Reads and publishes the availability of <paramref name="quotaIds"/>. Never throws: a failed publish only leaves a topic stale.</summary>
    public async Task PublishAsync(ISparkInventory inventory, IReadOnlyCollection<string> quotaIds, CancellationToken cancellationToken)
    {
        foreach (var quotaId in quotaIds)
        {
            try
            {
                var availability = await inventory.GetAvailabilityAsync(quotaId, cancellationToken);
                topics.Publish(Topic(quotaId), new InventoryAvailabilityFrame(quotaId, availability.Available, availability.Available == 0));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing the availability of quota {QuotaId} failed; its live topic stays stale until the next change.", quotaId);
            }
        }
    }
}

/// <summary>Anyone may follow a quota's availability, anonymous visitors included; there is no private stream.</summary>
internal sealed class InventoryLiveTopicPolicy : ISparkLiveTopicPolicy
{
    public string Prefix => InventoryAvailabilityPublisher.TopicPrefix;

    public bool CanSubscribe(string topic, SparkLiveViewer viewer) => true;

    public bool CanSubscribePrivate(string topic, SparkLiveViewer viewer) => false;
}
