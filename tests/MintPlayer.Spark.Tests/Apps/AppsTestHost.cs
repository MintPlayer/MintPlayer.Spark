using MintPlayer.Spark;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Messaging;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Tests.Organizations;

namespace MintPlayer.Spark.Tests.Apps;

[WebhookEvent("order.shipped", Permission = "orders:read", Description = "An order left the warehouse.")]
public sealed record OrderShipped(string OrderId, int Items, [property: Description("The carrier's name.")] string? Carrier);

[WebhookEvent("invoice.paid")]
public sealed record InvoicePaid(string InvoiceId, decimal Amount, DateTimeOffset PaidAt);

/// <summary>
/// The organizations host (signed-in users by header) with Messaging, the Apps package and a fake receiver behind the
/// package's SSRF-safe client. Deliveries retry after 200 ms, at most 3 attempts.
/// </summary>
public abstract class AppsTestHost : OrganizationsTestHost
{
    internal const string ReceiverUrl = "https://hooks.example.com/in";

    internal FakeReceiver Receiver { get; } = new();

    internal List<(string EndpointId, string Reason)> Disabled { get; } = [];

    protected AppsTestHost()
    {
        ExtraSpark = spark =>
        {
            spark.AddMessaging(o => o.FallbackPollInterval = TimeSpan.FromMilliseconds(250));
            spark.AddApps(apps =>
            {
                apps.AddEvent<OrderShipped>()
                    .AddEvent<InvoicePaid>()
                    .ConfigureOptions(o =>
                    {
                        o.CatalogTitle = "Shop webhooks";
                        o.MaxPayloadBytes = 2048;
                        ConfigureApps(o);
                    });
                ConfigureAppsBuilder(apps);
            });
            ConfigureSpark(spark);
            spark.Services.AddHttpClient(SparkAppsExtensions.DeliveryHttpClient).ConfigurePrimaryHttpMessageHandler(() => Receiver);
            spark.Services.Configure<SparkMessagingOptions>(m => m.Queues[SparkAppsQueues.Deliveries] = new SparkQueueOptions
            {
                MaxAttempts = 3,
                Backoff = [TimeSpan.FromMilliseconds(200)],
                MaxConcurrency = 16,
            });
            spark.Services.AddSingleton<IWebhookEndpointDisabledHandler>(new RecordingDisabledHandler(Disabled));
        };
    }

    protected virtual void ConfigureApps(SparkAppsOptions options) { }

    protected virtual void ConfigureAppsBuilder(AppsBuilder apps) { }

    protected virtual void ConfigureSpark(MintPlayer.Spark.Abstractions.Builder.ISparkBuilder spark) { }

    internal IWebhookPublisher Publisher(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWebhookPublisher>();

    /// <summary>Publishes <paramref name="data"/> for <paramref name="owner"/> and commits; returns the webhook-id.</summary>
    internal async Task<string?> PublishAsync(object data, WebhookOwner owner)
    {
        using var scope = Factory.CreateScope();
        using var session = Store.OpenAsyncSession();
        var id = await Publisher(scope).PublishAsync(data, owner, session.AsSparkSession());
        await session.SaveChangesAsync();
        return id;
    }

    /// <summary>Registers a webhook for <paramref name="owner"/> as <paramref name="userId"/>; returns its id and secret.</summary>
    internal async Task<(string Id, string Secret)> CreateEndpointAsync(string userId, WebhookOwner owner, string[]? events = null, string url = ReceiverUrl)
    {
        var (status, body) = await SendAsync("/spark/apps/webhooks/endpoints", new { ownerKind = owner.Kind, ownerKey = owner.Key, url, events = events ?? ["*"] }, userId);
        status.Should().Be(HttpStatusCode.OK, "the owner manages its own webhooks: " + body);
        return (body.GetProperty("endpoint").GetProperty("id").GetString()!, body.GetProperty("secret").GetString()!);
    }

    internal async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url, string? userId)
    {
        using var client = Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (userId is not null)
            request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    internal async Task<List<WebhookDelivery>> DeliveriesAsync(string endpointId)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.Advanced.LoadStartingWithAsync<WebhookDelivery>(WebhookDelivery.PrefixFor(endpointId), pageSize: 1024)).ToList();
    }

    /// <summary>How often <see cref="IWebhookEndpointDisabledHandler"/> was told about <paramref name="endpointId"/>.</summary>
    internal int DisabledCount(string endpointId)
    {
        lock (Disabled)
            return Disabled.Count(d => d.EndpointId == endpointId);
    }

    /// <summary>
    /// Waits for the disabled handler. The engine tells it last, after the endpoint and the delivery are saved (and, for a
    /// halt, after the scheduler's completion), so a wait on <c>Enabled</c> or on a settled delivery can still be before it.
    /// </summary>
    internal Task UntilDisabledHandlerToldAsync(string endpointId)
        => Until(() => Task.FromResult(DisabledCount(endpointId) > 0), $"the disabled handler was told about {endpointId}");

    internal async Task<WebhookEndpoint> EndpointAsync(string endpointId)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdPrefix + endpointId))!;
    }

    /// <summary>Polls <paramref name="probe"/> until it holds; a failure bound, not a wait.</summary>
    internal static async Task Until(Func<Task<bool>> probe, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Not within {seconds} s: {what}.");
    }

    internal async Task UntilSettledAsync(string endpointId, int count)
    {
        try
        {
            await Until(async () => (await DeliveriesAsync(endpointId)).Count(d => d.Status != WebhookDeliveryStatus.Pending) >= count, $"{count} settled deliveries on {endpointId}");
        }
        catch (TimeoutException ex)
        {
            using var session = Store.OpenAsyncSession();
            var chain = await session.LoadAsync<WebhookChain>(WebhookChain.IdFor(endpointId).Value);
            var state = string.Join("; ", (await DeliveriesAsync(endpointId)).Select(d => $"{d.Id} {d.Status} attempts={d.Attempts.Count}"));
            throw new TimeoutException($"{ex.Message} Deliveries: {state}. Chain: {System.Text.Json.JsonSerializer.Serialize(chain)}", ex);
        }
    }

    /// <summary>A receiver that records every request and answers per <see cref="Respond"/> (204 by default).</summary>
    internal sealed class FakeReceiver : HttpMessageHandler
    {
        public sealed record Request(string Url, Dictionary<string, string> Headers, byte[] Body, DateTime AtUtc)
        {
            public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
            public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        }

        public ConcurrentQueue<Request> Received { get; } = new();

        /// <summary>The answer to the n-th request (0-based); null: 204.</summary>
        public Func<Request, int, HttpResponseMessage?>? Respond { get; set; }

        /// <summary>When set, answers every request instead of <see cref="Respond"/> (e.g. by forwarding it to a real receiver).</summary>
        public Func<Request, Task<HttpResponseMessage>>? Forward { get; set; }

        private int count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var recorded = new Request(request.RequestUri!.ToString(), headers, body, DateTime.UtcNow);
            Received.Enqueue(recorded);
            var n = Interlocked.Increment(ref count) - 1;
            if (Forward is { } forward)
                return await forward(recorded);
            return Respond?.Invoke(recorded, n) ?? new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class RecordingDisabledHandler(List<(string, string)> disabled) : IWebhookEndpointDisabledHandler
    {
        public Task OnDisabledAsync(WebhookEndpoint endpoint, string reason, CancellationToken cancellationToken)
        {
            lock (disabled)
                disabled.Add((endpoint.PublicId, reason));
            return Task.CompletedTask;
        }
    }
}
