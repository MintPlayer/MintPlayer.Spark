namespace MintPlayer.Spark.Apps.Models;

/// <summary>
/// A direct webhook: an HTTPS URL an owner registered, with the events it wants. Id <c>WebhookEndpoints/ep_…</c>; the
/// <c>ep_…</c> part is the public endpoint id receivers see (and <c>v2a</c> signs over).
/// </summary>
public sealed class WebhookEndpoint
{
    public const string IdPrefix = "WebhookEndpoints/";

    public string? Id { get; set; }
    public string OwnerKind { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Event names, or <c>*</c> for every event.</summary>
    public List<string> Events { get; set; } = [];

    public bool Enabled { get; set; } = true;
    public string? DisabledReason { get; set; }

    /// <summary>The <c>whsec_</c> secret, Data-Protection encrypted (<c>sdp1:</c>, D15).</summary>
    public string ProtectedSecret { get; set; } = string.Empty;

    /// <summary>The secret before the last rotation; it keeps signing until <see cref="PreviousSecretExpiresUtc"/>.</summary>
    public string? PreviousProtectedSecret { get; set; }
    public DateTime? PreviousSecretExpiresUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>The first failure since the last success; drives auto-disable.</summary>
    public DateTime? FailingSinceUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastFailureUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }

    public string PublicId => Id is null ? string.Empty : Id[IdPrefix.Length..];

    public WebhookOwner Owner => new(OwnerKind, OwnerKey);

    public bool Subscribes(string eventType) => Events.Contains("*") || Events.Contains(eventType);
}

/// <summary>
/// The webhook endpoints of one owner, so an event finds its destinations by id, without an index (the libraries
/// ship none). Id <c>WebhookOwners/{owner token}</c>.
/// </summary>
public sealed class WebhookOwnerRegistry
{
    public const string IdPrefix = "WebhookOwners/";

    public string? Id { get; set; }
    public string OwnerKind { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;
    public List<string> EndpointIds { get; set; } = [];

    public static string IdFor(WebhookOwner owner) => IdPrefix + owner.IdToken;
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<WebhookDeliveryStatus>))]
public enum WebhookDeliveryStatus
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>One try: when, what came back, how long it took.</summary>
public sealed class WebhookDeliveryAttempt
{
    public DateTime AtUtc { get; set; }
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }

    /// <summary>The first bytes of the response, for the delivery log.</summary>
    public string? ResponseBody { get; set; }
}

/// <summary>
/// One event to one endpoint, with its payload and every attempt (D23). Id
/// <c>WebhookDeliveries/{ep}/{newest-first key}_{msg id}</c>: listing an endpoint's log is a prefix load, newest first.
/// Expires after <see cref="SparkAppsOptions.DeliveryRetention"/>.
/// </summary>
public sealed class WebhookDelivery : MintPlayer.Spark.Abstractions.IExpiring
{
    public const string IdPrefix = "WebhookDeliveries/";

    public string? Id { get; set; }

    /// <summary>Created + <see cref="SparkAppsOptions.DeliveryRetention"/>; Spark's stamper writes <c>@expires</c>.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>The <c>webhook-id</c>: the same for every attempt and every redelivery of this event.</summary>
    public string MessageId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>The envelope JSON exactly as signed and sent.</summary>
    public string Payload { get; set; } = string.Empty;

    public WebhookDeliveryStatus Status { get; set; }
    public List<WebhookDeliveryAttempt> Attempts { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    /// <summary>For a redelivery: the delivery it repeats.</summary>
    public string? RedeliveryOf { get; set; }

    /// <summary>True while it waits in an ordered chain (D22).</summary>
    public bool Ordered { get; set; }

    public string PublicId => Id is null ? string.Empty : Id[IdPrefix.Length..];

    /// <summary>The log id for <paramref name="messageId"/> on <paramref name="endpointPublicId"/>, sorted newest first.</summary>
    public static string IdFor(string endpointPublicId, DateTime createdUtc, string messageId, string? suffix = null)
        => $"{IdPrefix}{endpointPublicId}/{long.MaxValue - createdUtc.Ticks:x16}_{messageId}{suffix}";

    public static string PrefixFor(string endpointPublicId) => $"{IdPrefix}{endpointPublicId}/";
}

/// <summary>
/// An ordered destination's chain (D22, S9): the delivery in flight and those waiting behind it, under optimistic
/// concurrency. Id <c>WebhookChains/{ep}</c>.
/// </summary>
public sealed class WebhookChain
{
    public const string IdPrefix = "WebhookChains/";

    public string? Id { get; set; }
    public string? InFlight { get; set; }
    public List<string> Waiting { get; set; } = [];

    /// <summary>Stopped by <see cref="WebhookExhaustedPolicy.Halt"/>: <see cref="InFlight"/> is the failed delivery.</summary>
    public bool Halted { get; set; }

    public static string IdFor(string endpointPublicId) => IdPrefix + endpointPublicId;
}
