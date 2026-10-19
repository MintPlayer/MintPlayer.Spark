using Microsoft.Extensions.Primitives;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Webhooks;

/// <summary>Which route a delivery came in on.</summary>
public enum WebhookRoute
{
    /// <summary><c>POST /spark/webhooks/{source}</c>: serves sources with <see cref="WebhookSourceBuilder.SharedRoute"/>.</summary>
    Shared,

    /// <summary>A package's own route (GitHub's <c>/api/github/webhooks</c>): serves sources without it.</summary>
    Own,
}

/// <summary>
/// The request side of every webhook route: reads the raw body, verifies it with the source's
/// strategy, and hands the delivery to the source's handler (enqueued once, or inline). A package
/// that maps its own route calls this from its endpoint's binding and handler, so the checks are the
/// same on every route.
/// </summary>
public interface IWebhookReceiver
{
    /// <summary>
    /// Reads the raw body for <paramref name="source"/>. Throws <see cref="EndpointBindingException"/>
    /// with 404 for a source this route does not serve and 413 for a body over the source's cap.
    /// Call it from an endpoint's <c>BindRequestAsync</c>.
    /// </summary>
    ValueTask<WebhookRequest> ReadAsync(
        string source,
        WebhookRoute route,
        IEnumerable<KeyValuePair<string, StringValues>> headers,
        Stream body,
        long? contentLength,
        CancellationToken cancellationToken);

    /// <summary>
    /// Verifies and dispatches a delivery: 415 for a content type the source does not accept, 401
    /// for a delivery that fails verification, 200 once the delivery is enqueued (or, inline, handled).
    /// </summary>
    Task<IResult> ReceiveAsync(string source, WebhookRequest request, CancellationToken cancellationToken);
}

internal sealed class WebhookReceiver(
    WebhookSourceCatalog catalog,
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<WebhookReceiver> logger) : IWebhookReceiver
{
    /// <summary>Never stored with a delivery: credentials of the caller, not part of the provider's message.</summary>
    private static readonly HashSet<string> UnstoredHeaders = new(StringComparer.OrdinalIgnoreCase) { "Cookie", "Authorization" };

    public ValueTask<WebhookRequest> ReadAsync(
        string source,
        WebhookRoute route,
        IEnumerable<KeyValuePair<string, StringValues>> headers,
        Stream body,
        long? contentLength,
        CancellationToken cancellationToken)
    {
        // Before a byte of the body is read: an unknown source, or one this route does not serve,
        // is a 404 whatever it sends.
        if (!catalog.TryGet(source, out var found) || found.SharedRoute != (route == WebhookRoute.Shared))
            throw new EndpointBindingException(StatusCodes.Status404NotFound, "Unknown webhook source.");

        return WebhookRequest.ReadAsync(headers, body, contentLength, found.MaxBodyBytes, cancellationToken);
    }

    public async Task<IResult> ReceiveAsync(string source, WebhookRequest request, CancellationToken cancellationToken)
    {
        if (!catalog.TryGet(source, out var found))
            return Results.NotFound();

        if (!found.AcceptsContentType(request.ContentType))
        {
            logger.LogWarning("Webhook source {Source}: refused content type {ContentType}.", found.Name, request.ContentType ?? "<none>");
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        var verification = found.Verification(services).Verify(request);
        if (!verification.IsAccepted)
        {
            if (verification.Rejection == WebhookRejectionReason.NoSecretConfigured)
                logger.LogError("Webhook source {Source}: no secret is configured, so every delivery is refused. Configure the source's secret.", found.Name);
            else
                logger.LogWarning("Webhook source {Source}: delivery refused ({Reason}).", found.Name, verification.Rejection);
            return Results.Unauthorized();
        }

        var delivery = new WebhookDelivery
        {
            Source = found.Name,
            DeliveryId = found.DeliveryId?.Invoke(request),
            Headers = request.Headers
                .Where(h => !UnstoredHeaders.Contains(h.Key))
                .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase),
            Body = request.Body,
            PayloadVerified = verification.PayloadVerified,
            ResourceId = verification.ResourceId,
            ReceivedAtUtc = timeProvider.GetUtcNow(),
        };

        if (found.Handling == WebhookHandling.Inline)
        {
            // An exception propagates as a 500, so the provider retries: the handler owns durability.
            await services.GetRequiredKeyedService<IWebhookHandler>(found.Name).HandleAsync(delivery, cancellationToken);
            return Results.Ok();
        }

        // The enqueue precedes the 200, and that order is load-bearing: a provider that has its 2xx
        // does not send again, so acknowledging first would lose the delivery if the enqueue then
        // failed. The opposite failure — enqueued, but the 200 never arrives — earns a redelivery,
        // which the delivery-id key absorbs.
        var bus = services.GetRequiredService<IMessageBus>();
        var options = new BroadcastOptions { ScrubPayloadOnTerminal = found.ScrubPayloadOnTerminal };
        if (string.IsNullOrWhiteSpace(delivery.DeliveryId))
        {
            await bus.BroadcastAsync(delivery, options, cancellationToken);
        }
        else
        {
            // Namespaced by source: two providers may well use the same id shape.
            await bus.BroadcastOnceAsync(delivery, DeduplicationKey(found.Name, delivery.DeliveryId), options, cancellationToken);
        }

        return Results.Ok();
    }

    /// <summary>The messaging deduplication key of a delivery: <c>{source}:{deliveryId}</c>.</summary>
    internal static string DeduplicationKey(string source, string deliveryId) => $"{source}:{deliveryId}";
}

/// <summary>Runs a queued delivery's source handler.</summary>
internal sealed class WebhookDeliveryRecipient(IServiceProvider services) : IRecipient<WebhookDelivery>
{
    public Task HandleAsync(WebhookDelivery message, CancellationToken cancellationToken = default)
    {
        // A source removed while its deliveries were queued can never be handled: dead-letter at once.
        var handler = services.GetKeyedService<IWebhookHandler>(message.Source)
            ?? throw new NonRetryableException($"No handler is registered for webhook source '{message.Source}'.");
        return handler.HandleAsync(message, cancellationToken);
    }
}
