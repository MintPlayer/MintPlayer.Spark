using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Apps.Services;
using MintPlayer.Spark.Http;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Apps.Messaging;

/// <summary>Told when an endpoint is disabled automatically (5 days of failures, 410 Gone, a halted chain), e.g. to mail its owner.</summary>
public interface IWebhookEndpointDisabledHandler
{
    Task OnDisabledAsync(WebhookEndpoint endpoint, string reason, CancellationToken cancellationToken);
}

/// <summary>Messaging retries a delivery that throws this; the attempt is already in the log.</summary>
public sealed class WebhookDeliveryFailedException(string message) : Exception(message);

/// <summary>
/// Sends one <see cref="WebhookDelivery"/> (D14): signs it with the endpoint's secret(s) and any extra signer, POSTs it
/// through the SSRF-safe client and logs the attempt.
/// <list type="bullet">
/// <item>2xx: delivered.</item>
/// <item>410 Gone: the endpoint is disabled; nothing is retried.</item>
/// <item>429/503 with Retry-After: parked for that long (not an attempt).</item>
/// <item>Anything else: retried with the queue's backoff; the last attempt marks the delivery failed.</item>
/// </list>
/// Several failures in a row open the destination's breaker for a short while; an endpoint failing for
/// <see cref="SparkAppsOptions.AutoDisableAfter"/> is disabled.
/// </summary>
[Register(typeof(IRecipient<WebhookDeliveryMessage>), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class WebhookDeliveryHandler : IRecipient<WebhookDeliveryMessage>
{
    /// <summary>D23: the log keeps a 2 KB response excerpt.</summary>
    private const int ResponseExcerptBytes = 2048;

    /// <summary>The endpoints this handling already disabled (changed after the save, not edited: the loaded copy stays untouched).</summary>
    private readonly HashSet<Id<WebhookEndpoint>> disabledNow = [];

    /// <summary>The field changes per endpoint id, applied after the delivery's save (<see cref="SaveAsync"/>).</summary>
    private readonly Dictionary<string, List<Action<WebhookEndpoint>>> endpointChanges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A versioned endpoint write that lost to a concurrent edit is retried this often on the fresh copy.</summary>
    private const int EndpointWriteAttempts = 5;

    [Inject] private readonly ISparkStore store;
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly WebhookSecretProtector secrets;
    [Inject] private readonly WebhookScheduler scheduler;
    [Inject] private readonly WebhookSigners signers;
    [Inject] private readonly IMessageContext context;
    [Inject] private readonly IOptions<SparkAppsOptions> options;
    [Inject] private readonly IOptions<SparkMessagingOptions> messaging;
    [Inject] private readonly IEnumerable<IWebhookEndpointDisabledHandler> disabledHandlers;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<WebhookDeliveryHandler> logger;

    private bool IsLastAttempt
    {
        get
        {
            var max = messaging.Value.Queues.TryGetValue(SparkAppsQueues.Deliveries, out var queue) && queue.MaxAttempts is int configured
                ? configured
                : messaging.Value.MaxAttempts;
            return context.AttemptCount >= max;
        }
    }

    public async Task HandleAsync(WebhookDeliveryMessage message, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenSession();
        var delivery = await session.LoadAsync<WebhookDelivery>(message.DeliveryId, cancellationToken);
        if (delivery is null || delivery.Status != WebhookDeliveryStatus.Pending)
            return; // expired from the log, or already settled (a duplicate message)

        var endpoint = await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdFor(delivery.EndpointId).Value, cancellationToken);
        if (endpoint is null || !endpoint.Enabled)
        {
            await SettleAsync(session, delivery, WebhookDeliveryStatus.Failed, endpoint is null ? "The endpoint was deleted." : "The endpoint is disabled.", null, cancellationToken);
            return;
        }

        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        if (endpoint.ConsecutiveFailures >= o.BreakerThreshold && endpoint.LastFailureUtc is { } last && now - last < o.BreakerWindow)
        {
            // Open breaker: not sent, and not counted against the endpoint either, or it would never close.
            delivery.Attempts.Add(new WebhookDeliveryAttempt { AtUtc = now, Error = "Not sent: the destination failed several times in a row (breaker open)." });
            await FailedAttemptAsync(session, delivery, endpoint, countAgainstEndpoint: false, cancellationToken);
            return;
        }

        var attempt = new WebhookDeliveryAttempt { AtUtc = now };
        delivery.Attempts.Add(attempt);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(delivery, endpoint, cancellationToken);
            attempt.StatusCode = (int)response.StatusCode;
            attempt.ResponseBody = await ExcerptAsync(response, cancellationToken);
        }
        catch (SafeHttpBlockedException ex)
        {
            attempt.Error = "Refused: " + ex.Message;
            attempt.DurationMs = stopwatch.ElapsedMilliseconds;
            await SettleAsync(session, delivery, WebhookDeliveryStatus.Failed, null, endpoint, cancellationToken, countFailure: true);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            attempt.Error = ex is TaskCanceledException ? "Timed out." : ex.Message;
        }
        attempt.DurationMs = stopwatch.ElapsedMilliseconds;

        using (response)
        {
            if (response is { IsSuccessStatusCode: true })
            {
                ChangeEndpoint(endpoint, e =>
                {
                    e.ConsecutiveFailures = 0;
                    e.FailingSinceUtc = null;
                    e.LastSuccessUtc = now;
                });
                await SettleAsync(session, delivery, WebhookDeliveryStatus.Succeeded, null, null, cancellationToken);
                return;
            }

            if (response?.StatusCode == HttpStatusCode.Gone)
            {
                DisableEndpoint(endpoint, "The receiver answered 410 Gone.");
                await SettleAsync(session, delivery, WebhookDeliveryStatus.Failed, null, null, cancellationToken);
                await NotifyDisabledAsync(endpoint, "The receiver answered 410 Gone.", cancellationToken);
                return;
            }

            if (response is { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } && RetryAfter(response) is { } delay)
            {
                await SaveAsync(session, cancellationToken);
                throw new RetryAfterException(delay, $"The receiver asked to wait {delay}.");
            }
        }

        await FailedAttemptAsync(session, delivery, endpoint, countAgainstEndpoint: true, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(WebhookDelivery delivery, WebhookEndpoint endpoint, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(delivery.Payload);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        var input = new WebhookSigningInput(delivery.MessageId, timestamp, payload, endpoint.PublicId);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("User-Agent", options.Value.UserAgent);
        request.Headers.TryAddWithoutValidation(WebhookHeaders.Id, delivery.MessageId);
        request.Headers.TryAddWithoutValidation(WebhookHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookHeaders.Signature, WebhookSignatureHeader.Create(signers.For(endpoint, secrets, time.GetUtcNow().UtcDateTime), input));
        return await httpClientFactory.CreateClient(SparkAppsExtensions.DeliveryHttpClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task FailedAttemptAsync(ISparkSession session, WebhookDelivery delivery, WebhookEndpoint endpoint, bool countAgainstEndpoint, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        string? disabled = null;
        if (countAgainstEndpoint)
        {
            CountFailure(endpoint, now);
            var failingSince = endpoint.FailingSinceUtc ?? now;
            if (endpoint.FailingSinceUtc is null)
                ChangeEndpoint(endpoint, e => e.FailingSinceUtc ??= now);
            if (now - failingSince >= options.Value.AutoDisableAfter)
            {
                disabled = $"Every delivery failed since {failingSince:u}.";
                DisableEndpoint(endpoint, disabled);
            }
        }

        if (disabled is not null || IsLastAttempt)
        {
            await SettleAsync(session, delivery, WebhookDeliveryStatus.Failed, null, null, cancellationToken);
            if (disabled is not null)
                await NotifyDisabledAsync(endpoint, disabled, cancellationToken);
            return;
        }

        await SaveAsync(session, cancellationToken);
        throw new WebhookDeliveryFailedException($"Delivery {delivery.Id} failed (attempt {context.AttemptCount}); retrying.");
    }

    /// <summary>Final state, saved; then an ordered chain moves on (or halts).</summary>
    private async Task SettleAsync(ISparkSession session, WebhookDelivery delivery, WebhookDeliveryStatus status, string? note, WebhookEndpoint? countFor, CancellationToken cancellationToken, bool countFailure = false)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (note is not null)
            delivery.Attempts.Add(new WebhookDeliveryAttempt { AtUtc = now, Error = note });
        if (countFailure && countFor is not null)
            CountFailure(countFor, now);
        delivery.Status = status;
        delivery.CompletedUtc = now;

        var halt = status == WebhookDeliveryStatus.Failed && delivery.Ordered && options.Value.OnExhausted == WebhookExhaustedPolicy.Halt;
        WebhookEndpoint? halted = null;
        if (halt && !disabledNow.Contains(WebhookEndpoint.IdFor(delivery.EndpointId))
            && await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdFor(delivery.EndpointId).Value, cancellationToken) is { Enabled: true } endpoint)
        {
            DisableEndpoint(endpoint, $"Ordered delivery halted: {delivery.EventType} ({delivery.MessageId}) could not be delivered.");
            halted = endpoint;
        }
        await SaveAsync(session, cancellationToken);
        await scheduler.CompleteAsync(delivery, halt, cancellationToken);
        if (halted is not null)
            await NotifyDisabledAsync(halted, "Ordered delivery halted.", cancellationToken);
    }

    private void DisableEndpoint(WebhookEndpoint endpoint, string reason)
    {
        if (disabledNow.Add(endpoint.Id))
            ChangeEndpoint(endpoint, e =>
            {
                e.Enabled = false;
                e.DisabledReason = reason;
            });
    }

    /// <summary>One more failure in a row, counted on the stored value: a concurrent delivery's count is not lost.</summary>
    private void CountFailure(WebhookEndpoint endpoint, DateTime now)
        => ChangeEndpoint(endpoint, e =>
        {
            e.ConsecutiveFailures++;
            e.LastFailureUtc = now;
        });

    /// <summary>
    /// Queues a change to some of the endpoint's fields. The endpoint document is also edited by its owner (rotation,
    /// URL, events); saving the loaded copy would overwrite those, so <see cref="SaveAsync"/> applies the change to the
    /// stored copy instead.
    /// </summary>
    private void ChangeEndpoint(WebhookEndpoint endpoint, Action<WebhookEndpoint> change)
    {
        if (!endpointChanges.TryGetValue(endpoint.Id.Value, out var changes))
            endpointChanges[endpoint.Id.Value] = changes = [];
        changes.Add(change);
    }

    /// <summary>
    /// Saves the delivery, then each changed endpoint (#536; RavenDB field patches before): loaded fresh, changed and
    /// stored at the version read, retried on a concurrent write, so an owner's edit is never overwritten. The endpoint
    /// writes follow the delivery's save rather than sharing it; a crash between the two loses a counter update,
    /// which the next delivery's outcome corrects.
    /// </summary>
    private async Task SaveAsync(ISparkSession session, CancellationToken cancellationToken)
    {
        await session.SaveChangesAsync(cancellationToken);
        foreach (var (id, changes) in endpointChanges)
        {
            for (var attempt = 1; ; attempt++)
            {
                using var endpointSession = store.OpenSession();
                endpointSession.OptimisticConcurrency = true;
                if (await endpointSession.LoadAsync<WebhookEndpoint>(id, cancellationToken) is not { } stored)
                    break; // deleted meanwhile
                foreach (var change in changes)
                    change(stored);
                try
                {
                    await endpointSession.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (SparkStoreConcurrencyException) when (attempt < EndpointWriteAttempts)
                {
                }
            }
        }
        endpointChanges.Clear();
    }

    private async Task NotifyDisabledAsync(WebhookEndpoint endpoint, string reason, CancellationToken cancellationToken)
    {
        logger.LogWarning("Webhook endpoint {EndpointId} disabled: {Reason}", endpoint.PublicId, reason);
        foreach (var handler in disabledHandlers)
        {
            try { await handler.OnDisabledAsync(endpoint, reason, cancellationToken); }
            catch (Exception ex) { logger.LogError(ex, "IWebhookEndpointDisabledHandler {Handler} failed.", handler.GetType().Name); }
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is not { } d)
            return null;
        // Bounded: a receiver cannot park a delivery for days.
        return d < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : d > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : d;
    }

    private static async Task<string?> ExcerptAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[ResponseExcerptBytes];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
            return read == 0 ? null : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SafeHttpBlockedException)
        {
            return null;
        }
    }
}
