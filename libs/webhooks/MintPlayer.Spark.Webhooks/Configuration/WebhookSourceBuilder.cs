using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Webhooks;

/// <summary>When a source's handler runs.</summary>
public enum WebhookHandling
{
    /// <summary>
    /// The default. The verified delivery is enqueued on Spark Messaging before the provider gets its
    /// 200 (deduplicated on the delivery id when there is one) and the handler runs from the queue,
    /// with retries. A provider is never told "done" while nothing durable holds the delivery.
    /// </summary>
    Queued,

    /// <summary>
    /// The handler runs inside the request, after verification, and the provider gets 200 when it
    /// returns (500 when it throws, so the provider retries). For a handler that does its own durable
    /// enqueue and deduplication: the GitHub source, whose processor fans one delivery out to typed
    /// envelopes keyed on <c>X-GitHub-Delivery</c>.
    /// </summary>
    Inline,
}

/// <summary>
/// Configures one webhook source in <see cref="SparkWebhooksExtensions.AddWebhookSource"/>: how its
/// deliveries are verified, where its delivery id is, and which handler receives them.
/// </summary>
public sealed partial class WebhookSourceBuilder
{
    /// <summary>The default body cap: 1 MiB.</summary>
    public const long DefaultMaxBodyBytes = 1024 * 1024;

    internal WebhookSourceBuilder(string name) => Name = name;

    /// <summary>The source's name: the last segment of its shared route, <c>/spark/webhooks/{name}</c>.</summary>
    public string Name { get; }

    /// <summary>
    /// Builds the verification strategy for one delivery, from the request's services (read secrets
    /// from options or configuration here, so a rotated secret takes effect without a restart).
    /// Required: there is no unverified source.
    /// </summary>
    public Func<IServiceProvider, IWebhookSignatureStrategy>? Verification { get; set; }

    /// <summary>
    /// Reads the provider's delivery id, for deduplication (<see cref="WebhookDeliveryIds"/>). A
    /// delivery whose id is found is enqueued at most once per source and id, so a redelivery after a
    /// timeout or a 5xx runs the handler once. <see langword="null"/>, or no id in a delivery: every
    /// delivery is enqueued. Leave it unset for fetch-by-id sources (see <see cref="FetchByIdStrategy"/>).
    /// </summary>
    public Func<WebhookRequest, string?>? DeliveryId { get; set; }

    /// <summary>The largest body accepted; larger answers 413 before it is read. Default <see cref="DefaultMaxBodyBytes"/>.</summary>
    public long MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;

    /// <summary>
    /// The media types accepted (<c>application/json</c>); anything else answers 415. Empty, the
    /// default, accepts any. Parameters such as <c>; charset=utf-8</c> are ignored.
    /// </summary>
    public IList<string> ContentTypes { get; } = [];

    /// <summary>
    /// Whether the source is served by the shared route <c>POST /spark/webhooks/{name}</c>. Default
    /// <see langword="true"/>. A package that maps its own route (GitHub keeps
    /// <c>/api/github/webhooks</c>) sets this to <see langword="false"/>, and the shared route then
    /// answers 404 for this name.
    /// </summary>
    public bool SharedRoute { get; set; } = true;

    /// <summary>
    /// Clear <c>PayloadJson</c> once the queued delivery is completed or dead-lettered (Spark
    /// Messaging's <c>ScrubPayloadOnTerminal</c>). Default <see langword="true"/>: payment and
    /// shipping payloads carry personal data that has no business outliving the handling. The
    /// message document itself stays, so deduplication still holds for the retention window.
    /// </summary>
    public bool ScrubPayloadOnTerminal { get; set; } = true;

    internal Type? HandlerType { get; private set; }

    internal WebhookHandling Handling { get; private set; }

    /// <summary>The source's handler. One per source; a second call throws.</summary>
    public WebhookSourceBuilder UseHandler<THandler>(WebhookHandling handling = WebhookHandling.Queued)
        where THandler : class, IWebhookHandler
    {
        if (HandlerType is not null)
            throw new InvalidOperationException(
                $"Webhook source '{Name}' already has a handler ({HandlerType.Name}); a source has exactly one. Compose inside it.");
        HandlerType = typeof(THandler);
        Handling = handling;
        return this;
    }

    internal WebhookSource Build()
    {
        if (Verification is null)
            throw new InvalidOperationException($"Webhook source '{Name}' has no Verification. Every source verifies its deliveries.");
        if (HandlerType is null)
            throw new InvalidOperationException($"Webhook source '{Name}' has no handler; call UseHandler<T>().");
        if (MaxBodyBytes is <= 0 or > int.MaxValue)
            throw new InvalidOperationException($"Webhook source '{Name}': MaxBodyBytes must be between 1 and {int.MaxValue}.");

        return new WebhookSource(
            Name,
            Verification,
            DeliveryId,
            MaxBodyBytes,
            ContentTypes.Select(t => t.Trim()).Where(t => t.Length > 0).ToArray(),
            SharedRoute,
            ScrubPayloadOnTerminal,
            HandlerType,
            Handling);
    }

    /// <summary>Lower-case letters, digits and <c>-</c>; 1–64 characters. Safe in a route and in a deduplication key.</summary>
    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex NamePattern();
}

/// <summary>A registered webhook source; built from <see cref="WebhookSourceBuilder"/>.</summary>
internal sealed record WebhookSource(
    string Name,
    Func<IServiceProvider, IWebhookSignatureStrategy> Verification,
    Func<WebhookRequest, string?>? DeliveryId,
    long MaxBodyBytes,
    IReadOnlyList<string> ContentTypes,
    bool SharedRoute,
    bool ScrubPayloadOnTerminal,
    Type HandlerType,
    WebhookHandling Handling)
{
    public bool AcceptsContentType(string? contentType)
    {
        if (ContentTypes.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(contentType))
            return false;
        var mediaType = contentType.Split(';', 2)[0].Trim();
        return ContentTypes.Any(t => string.Equals(t, mediaType, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Every registered source, by name. A singleton instance, filled at registration time.</summary>
internal sealed class WebhookSourceCatalog
{
    private readonly Dictionary<string, WebhookSource> sources = new(StringComparer.Ordinal);

    public bool HasSharedRouteSource => sources.Values.Any(s => s.SharedRoute);

    public IEnumerable<WebhookSource> Sources => sources.Values;

    public void Add(WebhookSource source)
    {
        if (!sources.TryAdd(source.Name, source))
            throw new InvalidOperationException($"Webhook source '{source.Name}' is registered twice.");
    }

    /// <summary>The source <paramref name="name"/>, matched case-insensitively (names are stored lower case).</summary>
    public bool TryGet(string? name, [MaybeNullWhen(false)] out WebhookSource source)
    {
        if (string.IsNullOrEmpty(name))
        {
            source = null;
            return false;
        }
        return sources.TryGetValue(name.ToLowerInvariant(), out source);
    }
}
