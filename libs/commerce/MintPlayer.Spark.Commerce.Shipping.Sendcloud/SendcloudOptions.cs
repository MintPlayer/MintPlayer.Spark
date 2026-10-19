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
    /// Further Sendcloud accounts by name (warehouse WD8: a carrier account per owner). A shipment's
    /// <c>Account</c> picks one; the keys above are the default account.
    /// </summary>
    public Dictionary<string, SendcloudAccount> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The shipping methods the account books, by Sendcloud shipping-method id (<c>GET /shipping_methods</c>), with
    /// what each accepts. Sendcloud's methods depend on the account's carrier contracts, so none is shipped: the host
    /// lists the ones it offers. A method with <c>ServicePoint</c> delivers to a service point (<c>to_service_point</c>).
    /// </summary>
    public Dictionary<string, CarrierServiceOptions> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sender address id configured in Sendcloud (<c>sender_address</c>), when the account uses one.</summary>
    public long? SenderAddressId { get; set; }

    /// <summary>The carriers whose service points are searched (<c>postnl</c>, <c>bpost</c>, <c>dpd</c>, …); all enabled ones when empty.</summary>
    public List<string> ServicePointCarriers { get; set; } = [];

    /// <summary>The label size to download: <c>label_printer</c> (A6) or <c>normal_printer</c> (A4 with up to four labels).</summary>
    public string LabelPrinter { get; set; } = "label_printer";

    /// <summary>The panel API base; overridable for tests only.</summary>
    public string ApiBaseUrl { get; set; } = "https://panel.sendcloud.sc/api/v2/";

    /// <summary>The service-point API base.</summary>
    public string ServicePointsBaseUrl { get; set; } = "https://servicepoints.sendcloud.sc/api/v2/";
}

/// <summary>One Sendcloud account's keys.</summary>
public sealed class SendcloudAccount
{
    public string? PublicKey { get; set; }

    public string? SecretKey { get; set; }
}
