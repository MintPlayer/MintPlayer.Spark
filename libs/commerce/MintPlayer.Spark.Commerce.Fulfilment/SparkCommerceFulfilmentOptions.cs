using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Fulfilment.Contracts;

namespace MintPlayer.Spark.Commerce.Fulfilment;

/// <summary>
/// <c>Spark:Commerce:Fulfilment</c> (commerce PRD §4.9, warehouse PRD §4.3–§4.4). Code given to
/// <c>AddCommerceFulfilment(o => …)</c> runs after the section.
/// </summary>
public sealed class SparkCommerceFulfilmentOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Fulfilment";

    /// <summary>
    /// This shop's name in the contracts' ids (<c>FulfilmentOrders/{ShopName}/…</c>), so ids are unique across every shop
    /// a warehouse serves (commerce §4.9). Required: letters, digits, <c>-</c>, <c>_</c> and <c>.</c>. Permanent once orders
    /// were published.
    /// </summary>
    public string ShopName { get; set; } = "";

    /// <summary>
    /// The owner of the goods the platform sells itself (an order line without a seller), as the warehouse's owner link
    /// knows it (Q-W23). Default: <see cref="ShopName"/>.
    /// </summary>
    public string? PlatformOwnerId { get; set; }

    /// <summary>
    /// A seller organization's owner id at the warehouse, when it differs from the organization id
    /// (<c>{ "Organizations/12-A": "acme" }</c>). A seller not listed is the owner under its organization id.
    /// </summary>
    public Dictionary<string, string> OwnerIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The channels the warehouse ships, with their stock settings (§4.4). Empty: every channel a shop product sells in.
    /// </summary>
    public Dictionary<string, FulfilmentChannelOptions> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What happens to quantity the warehouse cannot ship (warehouse §4.3). Default <see cref="FulfilmentBackorderPolicy.Allow"/>.</summary>
    public FulfilmentBackorderPolicy BackorderPolicy { get; set; } = FulfilmentBackorderPolicy.Allow;

    /// <summary>Keep an order unallocated until every line can be allocated. Default false.</summary>
    public bool ShipComplete { get; set; }

    /// <summary>
    /// When a withheld refund of warehouse-backed goods is paid (WD12, commerce §4.9). Default
    /// <see cref="FulfilmentRefundTiming.OnReceipt"/>: on the warehouse's receipt or, for a withdrawal, on the buyer's proof
    /// of dispatch, whichever is earlier (CRD Art. 13(3), GA1; staff register the proof in Checkout's aftercare).
    /// </summary>
    public FulfilmentRefundTiming RefundTiming { get; set; } = FulfilmentRefundTiming.OnReceipt;

    /// <summary>
    /// The reconcile (six fields): re-derives every open order from the stored replicas, which is the only path an ETL
    /// transport has (ETL raises no change notifications), and repairs what a lost notification missed. Default every minute.
    /// Read at registration.
    /// </summary>
    public string ReconcileSchedule { get; set; } = "0 * * * * *";

    /// <summary>
    /// The catalog and stock sweep (six fields): publishes the catalog items of changed variants and re-applies every stock
    /// level whose version was not applied yet. Default every five minutes. Read at registration.
    /// </summary>
    public string CatalogSchedule { get; set; } = "0 */5 * * * *";

    /// <summary>Open orders one reconcile run handles at most. Default 200.</summary>
    public int ReconcileBatchSize { get; set; } = 200;

    /// <summary>
    /// How long after its last shipment an order whose delivery the carrier never reported stops being reconciled. A
    /// later return re-opens it. Default 60 days.
    /// </summary>
    public TimeSpan DeliveryWatch { get; set; } = TimeSpan.FromDays(60);

    /// <summary>The recall notice mail (GA7). Default <c>SparkFulfilment/RecallNotice</c>.</summary>
    public string RecallMailTemplate { get; set; } = "SparkFulfilment/RecallNotice";

    /// <summary>The owner id of a seller (null: the platform).</summary>
    public string OwnerOf(string? sellerOrganizationId)
        => sellerOrganizationId is null ? PlatformOwnerId is { Length: > 0 } platform ? platform : ShopName
            : OwnerIds.TryGetValue(sellerOrganizationId, out var mapped) && mapped.Length > 0 ? mapped : sellerOrganizationId;

    /// <summary>Whether the warehouse ships <paramref name="channel"/>.</summary>
    public bool Ships(string channel) => Channels.Count == 0 || Channels.ContainsKey(channel);

    /// <summary>The channel's safety buffer (§4.4), 0 when none is configured.</summary>
    public int BufferOf(string channel) => Channels.TryGetValue(channel, out var c) ? Math.Max(0, c.Buffer) : 0;
}

/// <summary>A channel the warehouse ships (<c>Spark:Commerce:Fulfilment:Channels:{name}</c>).</summary>
public sealed class FulfilmentChannelOptions
{
    /// <summary>
    /// Units held back from the warehouse's figures (warehouse §4.4: the lever that bounds oversell across channels during
    /// the replication lag). Default 0, exact for a SKU sold through one channel.
    /// </summary>
    public int Buffer { get; set; }
}

/// <summary>When a refund of goods the warehouse ships is paid (WD12).</summary>
public enum FulfilmentRefundTiming
{
    /// <summary>When the warehouse reports the goods received, or on the buyer's proof of dispatch (a withdrawal), whichever is earlier.</summary>
    OnReceipt,
    /// <summary>As soon as the return is accepted: the refund is not withheld (the host takes the risk).</summary>
    OnRequest,
}
