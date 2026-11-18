using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.Receiver;
using Generated = MintPlayer.Spark.Tests.Apps.Generated;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 D8 (<c>docs/apps_webhooks_PRD.md</c>): the Spark receiver hands an event it cannot type, or one nobody receives
/// typed, to an <see cref="UnknownWebhookMessage"/> recipient, with the raw data. One path takes one receiver.
/// </summary>
public class WebhookReceiverUnknownEventTests : AppsTestHost
{
    private const string ReceiverPath = "/webhooks/unknown";
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());

    private readonly UnknownRecorder unknown = new();
    private Exception? secondRegistration;

    protected override void ConfigureSpark(ISparkBuilder spark)
    {
        spark.AddWebhookReceiver(ReceiverPath, Generated.ShopWebhooksProcessor.EventTypes, o => o.Secrets.Add(Secret));
        try
        {
            spark.AddWebhookReceiver(ReceiverPath, Generated.ShopWebhooksProcessor.EventTypes, o => o.Secrets.Add(Secret));
        }
        catch (InvalidOperationException e)
        {
            secondRegistration = e;
        }
        // No IRecipient<WebhookMessage<OrderShipped>>: a known type nobody receives typed goes out as unknown too.
        spark.Services.AddSingleton<IRecipient<UnknownWebhookMessage>>(unknown);
    }

    [Fact]
    public async Task An_untyped_or_unreceived_event_reaches_the_unknown_recipient_with_its_raw_data()
    {
        using var client = Factory.CreateClient();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var closed = """{"type":"shop.closed","timestamp":"2026-11-18T10:00:00Z","data":{"reason":"night"}}"""u8.ToArray();
        (await client.SendAsync(Request(closed, "msg_closed", now))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var shipped = """{"type":"order.shipped","timestamp":"2026-11-18T11:00:00Z","data":{"orderId":"o","items":1,"carrier":null}}"""u8.ToArray();
        (await client.SendAsync(Request(shipped, "msg_shipped", now))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await Until(() => Task.FromResult(unknown.Received.Count >= 2), "both events reached the unknown recipient");
        var byType = unknown.Received.ToDictionary(m => m.Type);
        var message = byType["shop.closed"];
        message.Receiver.Should().Be(ReceiverPath);
        message.MessageId.Should().Be("msg_closed");
        message.OccurredAt.Should().Be(DateTimeOffset.Parse("2026-11-18T10:00:00Z"));
        message.DataJson.Should().Be("""{"reason":"night"}""");
        byType["order.shipped"].DataJson.Should().Contain("\"orderId\":\"o\"", "a known type without a typed recipient is not lost");
    }

    [Fact]
    public void A_second_receiver_on_the_same_path_is_refused()
    {
        using var client = Factory.CreateClient();
        secondRegistration.Should().NotBeNull("two receivers on one path would share one route");
        secondRegistration!.Message.Should().Contain(ReceiverPath);
    }

    private static HttpRequestMessage Request(byte[] body, string id, long timestamp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ReceiverPath) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var hmac = new HMACSHA256(MintPlayer.Webhooks.WebhookSecret.Parse(Secret).GetKey());
        request.Headers.Add("webhook-signature", "v1," + Convert.ToBase64String(hmac.ComputeHash([.. Encoding.UTF8.GetBytes($"{id}.{timestamp}."), .. body])));
        return request;
    }

    private sealed class UnknownRecorder : IRecipient<UnknownWebhookMessage>
    {
        public ConcurrentQueue<UnknownWebhookMessage> Received { get; } = new();

        public Task HandleAsync(UnknownWebhookMessage message, CancellationToken cancellationToken = default)
        {
            Received.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
