using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Apps.Models;

/// <summary>
/// A direct webhook: an HTTPS URL an owner registered, with the events it wants. Id <c>webhookendpoints/ep_…</c>; the
/// <c>ep_…</c> part is the public endpoint id receivers see (and <c>v2a</c> signs over). It is lower-case hex, so the
/// lower-cased id of an endpoint created before #536 (<c>WebhookEndpoints/ep_…</c>) keeps the same public id.
/// </summary>
/// <remarks>
/// The owner registry and an App keep ids as strings in their stored casing (<see cref="WebhookOwnerRegistry.EndpointIds"/>,
/// <see cref="WebhookChain.InFlight"/>): compare a loaded id with them ignoring case (#536 D7).
/// </remarks>
[IdKind(SparkIdKind.Natural)]
public sealed partial class WebhookEndpoint
{
    public const string IdPrefix = "webhookendpoints/";

    public Id<WebhookEndpoint> Id { get; set; }
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

    /// <summary>
    /// Set when this is an App's webhook (D29): the App decides its URL and events, it is not in the owner's registry, and
    /// only installations (M5) and pings send to it.
    /// </summary>
    public string? AppSlug { get; set; }

    public string PublicId => Id.IsEmpty ? string.Empty : Id.Value[IdPrefix.Length..];

    /// <summary>The endpoint with public id <paramref name="publicId"/> (<c>ep_…</c>).</summary>
    public static Id<WebhookEndpoint> IdFor(string publicId) => Id<WebhookEndpoint>.FromTrusted(IdPrefix + publicId);

    public WebhookOwner Owner => new(OwnerKind, OwnerKey);

    public bool Subscribes(string eventType) => Events.Contains("*") || Events.Contains(eventType);
}

/// <summary>
/// The webhook endpoints of one owner, so an event finds its destinations by id, without an index (the libraries
/// ship none). Id <c>webhookowners/{owner token}</c>.
/// </summary>
/// <remarks>
/// The token hashes the owner's key as given, so the key must always come from the stored reference
/// (an entity's <c>OrganizationId</c>, a membership's), never from a loaded id: an organization created
/// before #536 keeps <c>Organizations/…</c> there while its loaded id is lower case (#536 D7).
/// </remarks>
[IdKind(SparkIdKind.Natural)]
public sealed partial class WebhookOwnerRegistry
{
    public const string IdPrefix = "webhookowners/";

    public Id<WebhookOwnerRegistry> Id { get; set; }
    public string OwnerKind { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;
    public List<string> EndpointIds { get; set; } = [];

    /// <summary>The Apps this owner registered (D29): <see cref="SparkApp"/> ids.</summary>
    public List<string> AppIds { get; set; } = [];

    /// <summary>The Apps installed on this owner (D30): <see cref="AppInstallation"/> ids. Events fan out to them too.</summary>
    public List<string> InstallationIds { get; set; } = [];

    /// <summary>Whether an event of this owner has anywhere to go.</summary>
    public bool HasDestinations => EndpointIds.Count > 0 || InstallationIds.Count > 0;

    public static Id<WebhookOwnerRegistry> IdFor(WebhookOwner owner) => Id<WebhookOwnerRegistry>.FromTrusted(IdPrefix + owner.IdToken);
}

/// <summary>
/// A fixed <c>webhook-id</c> that was published (D34): created only if absent, in the session of the publish, so a publisher
/// that runs twice for one event queues it once. Delivery ids carry the publish time, so the fan-out alone cannot see
/// the repeat. Id <c>webhookeventkeys/{msg id}</c>; expires with the deliveries.
/// </summary>
[IdKind(SparkIdKind.Natural)]
public sealed partial class WebhookEventKey : IExpiring
{
    public const string IdPrefix = "webhookeventkeys/";

    public Id<WebhookEventKey> Id { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    public static Id<WebhookEventKey> IdFor(string messageId) => Id<WebhookEventKey>.FromTrusted(IdPrefix + messageId);
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
/// <c>webhookdeliveries/{ep}/{newest-first key}_{msg id}</c>: listing an endpoint's log is a prefix load, newest first.
/// Expires after <see cref="SparkAppsOptions.DeliveryRetention"/>.
/// </summary>
/// <remarks>
/// The id is lower-cased (#536 D7), so a caller's mixed-case <c>webhook-id</c> shows lower case in the public delivery id;
/// <see cref="MessageId"/> keeps it exactly, and that is what is sent.
/// </remarks>
[IdKind(SparkIdKind.Natural)]
public sealed partial class WebhookDelivery : IExpiring
{
    public const string IdPrefix = "webhookdeliveries/";

    public Id<WebhookDelivery> Id { get; set; }

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

    public string PublicId => Id.IsEmpty ? string.Empty : Id.Value[IdPrefix.Length..];

    /// <summary>The log id for <paramref name="messageId"/> on <paramref name="endpointPublicId"/>, sorted newest first.</summary>
    public static Id<WebhookDelivery> IdFor(string endpointPublicId, DateTime createdUtc, string messageId, string? suffix = null)
        => Id<WebhookDelivery>.FromTrusted($"{IdPrefix}{endpointPublicId}/{long.MaxValue - createdUtc.Ticks:x16}_{messageId}{suffix}");

    /// <summary>The delivery with public id <paramref name="publicId"/>.</summary>
    public static Id<WebhookDelivery> IdFor(string publicId) => Id<WebhookDelivery>.FromTrusted(IdPrefix + publicId);

    public static string PrefixFor(string endpointPublicId) => $"{IdPrefix}{endpointPublicId}/";
}

/// <summary>
/// An ordered destination's chain (D22, S9): the delivery in flight and those waiting behind it, under optimistic
/// concurrency. Id <c>webhookchains/{ep}</c>.
/// </summary>
/// <remarks>
/// <see cref="InFlight"/> and <see cref="Waiting"/> hold delivery ids as stored: one queued before #536 keeps
/// <c>WebhookDeliveries/…</c>. Compare and order them with a loaded delivery's id ignoring case (#536 D7).
/// </remarks>
[IdKind(SparkIdKind.Natural)]
public sealed partial class WebhookChain
{
    public const string IdPrefix = "webhookchains/";

    public Id<WebhookChain> Id { get; set; }
    public string? InFlight { get; set; }
    public List<string> Waiting { get; set; } = [];

    /// <summary>Stopped by <see cref="WebhookExhaustedPolicy.Halt"/>: <see cref="InFlight"/> is the failed delivery.</summary>
    public bool Halted { get; set; }

    public static Id<WebhookChain> IdFor(string endpointPublicId) => Id<WebhookChain>.FromTrusted(IdPrefix + endpointPublicId);
}
