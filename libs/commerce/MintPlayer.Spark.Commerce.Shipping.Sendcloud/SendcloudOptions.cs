using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Shipping.Sendcloud;

/// <summary>
/// <c>Spark:Commerce:Shipping:Sendcloud</c>. Credentials come from configuration or user-secrets only; they are read
/// per call (<c>IOptionsMonitor</c>), so a rotated key takes effect without a restart, and never logged.
/// </summary>
public sealed class SendcloudOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Shipping:Sendcloud";

    /// <summary>The integration's public key (HTTP Basic user name).</summary>
    public string? PublicKey { get; set; }

    /// <summary>The integration's secret key (HTTP Basic password). It also signs the webhooks (<c>Sendcloud-Signature</c>).</summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// The integration's webhook signature key, when Sendcloud shows one apart from the secret key; webhooks are then
    /// accepted when signed with either.
    /// </summary>
    public string? WebhookSignatureKey { get; set; }

    /// <summary>
    /// Further Sendcloud accounts by name (warehouse WD8: a carrier account per owner). A shipment's
    /// <c>Account</c> picks one; the keys above are the default account.
    /// </summary>
    public Dictionary<string, SendcloudAccount> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The shipping options the account books, by Sendcloud <c>shipping_option_code</c> (<c>postnl:standard</c>,
    /// <c>dpd:home</c>; listed by <c>POST /api/v3/shipping-options</c>), with what each accepts. They depend on the
    /// account's carrier contracts, so none is shipped: the host lists the ones it offers. An option with
    /// <c>ServicePoint</c> delivers to a service point (<c>to_service_point</c>); one with <c>Return</c> books through
    /// the Returns API.
    /// </summary>
    public Dictionary<string, CarrierServiceOptions> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The carrier contract to book a shipping option under (<c>ship_with.properties.contract_id</c>), by option code,
    /// when the account has several contracts for its carrier. Absent: Sendcloud picks the account's default.
    /// </summary>
    public Dictionary<string, long> ContractIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The sender address id configured in Sendcloud. When set, outbound shipments send
    /// <c>from_address: {"sender_address_id": …}</c> instead of the ship-from address.
    /// </summary>
    public long? SenderAddressId { get; set; }

    /// <summary>The carriers whose service points are searched (<c>postnl</c>, <c>bpost</c>, <c>dpd</c>, …); the integration's enabled carriers when empty.</summary>
    public List<string> ServicePointCarriers { get; set; } = [];

    /// <summary>The paper size labels are downloaded at: <c>A6</c> (label printers) or <c>A4</c>.</summary>
    public string PaperSize { get; set; } = "A6";

    /// <summary>The resolution of a ZPL label (Sendcloud accepts 203, 300 or 600).</summary>
    public int ZplDpi { get; set; } = 203;

    /// <summary>The API v3 base (shipments, returns, parcel documents, tracking, service points); overridable for tests only.</summary>
    public string ApiBaseUrl { get; set; } = "https://panel.sendcloud.sc/api/v3/";
}

/// <summary>One Sendcloud account's keys.</summary>
public sealed class SendcloudAccount
{
    public string? PublicKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>The account's webhook signature key, when it differs from the secret key.</summary>
    public string? WebhookSignatureKey { get; set; }
}
