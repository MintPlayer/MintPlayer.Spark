using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;
using MintPlayer.Spark.Messaging.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Webhooks;
using Newtonsoft.Json;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Webhooks;

/// <summary>
/// The request side of <c>MintPlayer.Spark.Webhooks</c> against a real message bus: a verified
/// delivery is enqueued once per delivery id, the bytes in the queue are the bytes that were signed,
/// and a delivery that fails verification enqueues nothing.
/// </summary>
public class WebhookReceiverTests : SparkTestDriver
{
    private static async Task<T> ThrowsAsync<T>(Func<Task> act) where T : Exception
        => (await act.Should().ThrowAsync<T>()).Which;

    private static T Throws<T>(Action act) where T : Exception
        => act.Should().Throw<T>().Which;

    private const string Secret = "receiver-test-secret";

    private static readonly HmacSignatureScheme Scheme = new("X-Test-Signature", WebhookHmacAlgorithm.Sha256, WebhookSignatureEncoding.Hex, "sha256=");

    /// <summary>
    /// Bytes no JSON round trip preserves: a trailing zero, an escaped slash, a non-UTC offset, a
    /// number beyond double precision, and a raw non-ASCII character.
    /// </summary>
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        """{"id":"evt_42","amount":1.50,"url":"a\/b","at":"2026-10-19T08:45:12.000+02:00","big":12345678901234567890123,"name":"Zoë"}""");

    private ServiceProvider BuildServices(Action<WebhookSourceBuilder>? configure = null, string name = "test")
    {
        var builder = new SparkBuilder(new ServiceCollection());
        builder.AddWebhookSource(name, source =>
        {
            source.Verification = _ => new HmacSignatureStrategy(Scheme, Secret);
            source.DeliveryId = WebhookDeliveryIds.FromHeader("X-Delivery");
            source.UseHandler<RecordingHandler>();
            configure?.Invoke(source);
        });
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IDocumentStore>(Store);
        builder.Services.AddSingleton<IMessageBus>(new MessageBus(Store, Options.Create(new SparkMessagingOptions())));
        builder.Services.AddSingleton<RecordingHandler.Log>();
        return builder.Services.BuildServiceProvider();
    }

    private static string Sign(byte[] body)
        => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));

    private static WebhookRequest Request(byte[] body, string? signature, string? deliveryId = "delivery-1", params (string Name, string Value)[] extra)
    {
        var headers = new List<KeyValuePair<string, StringValues>> { new("Content-Type", "application/json") };
        if (signature is not null)
            headers.Add(new("X-Test-Signature", signature));
        if (deliveryId is not null)
            headers.Add(new("X-Delivery", deliveryId));
        headers.AddRange(extra.Select(h => new KeyValuePair<string, StringValues>(h.Name, h.Value)));
        return new WebhookRequest(headers, body);
    }

    private static async Task<int> StatusOf(Task<IResult> result) => ((IStatusCodeHttpResult)await result).StatusCode ?? 200;

    private async Task<List<SparkMessage>> QueuedAsync()
    {
        await Store.WaitForIndexingAsync();
        using var session = Store.OpenAsyncSession();
        return await session.Query<SparkMessage>().ToListAsync();
    }

    [Fact]
    public async Task A_verified_delivery_is_enqueued_and_answered_200()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();

        (await StatusOf(receiver.ReceiveAsync("test", Request(Body, Sign(Body)), default))).Should().Be(200);

        var messages = await QueuedAsync();
        messages.Should().ContainSingle();
        messages[0].QueueName.Should().Be(WebhookDelivery.QueueName);
    }

    [Fact]
    public async Task A_duplicate_delivery_is_enqueued_once()
    {
        await using var services = BuildServices();

        // Two requests, as a provider's retry after a lost 200 would send them: separate scopes,
        // same delivery id, same bytes.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();
            (await StatusOf(receiver.ReceiveAsync("test", Request(Body, Sign(Body), "delivery-dup"), default))).Should().Be(200,
                "a duplicate is acknowledged, or the provider keeps retrying it");
        }

        var messages = await QueuedAsync();
        messages.Should().ContainSingle("the second delivery carries an id already enqueued, so the handler runs once");
        messages[0].Id.Should().Be(MessageBus.DeduplicationId(typeof(WebhookDelivery), "test:delivery-dup"));
    }

    [Fact]
    public async Task Distinct_delivery_ids_are_each_enqueued()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();

        await receiver.ReceiveAsync("test", Request(Body, Sign(Body), "delivery-a"), default);
        await receiver.ReceiveAsync("test", Request(Body, Sign(Body), "delivery-b"), default);

        (await QueuedAsync()).Count.Should().Be(2);
    }

    [Fact]
    public async Task The_queued_body_is_the_exact_signed_bytes()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();
        var signature = Sign(Body);

        await receiver.ReceiveAsync("test", Request(Body, signature), default);

        // Read it back the way Messaging's processor does: PayloadJson through Newtonsoft into the type.
        var message = (await QueuedAsync()).Should().ContainSingle().Which;
        var delivery = (WebhookDelivery)JsonConvert.DeserializeObject(message.PayloadJson!, typeof(WebhookDelivery))!;

        delivery.Body.Should().Equal(Body);
        var text = delivery.ReadBodyText();
        text.Should().Contain("1.50");
        text.Should().Contain("a\\/b");
        text.Should().Contain(".000+02:00");
        text.Should().Contain("12345678901234567890123");
        new HmacSignatureStrategy(Scheme, Secret).VerifyHeader(signature, delivery.Body).IsAccepted
            .Should().BeTrue("the handler holds the bytes the provider signed");
        delivery.Source.Should().Be("test");
        delivery.DeliveryId.Should().Be("delivery-1");
        delivery.PayloadVerified.Should().BeTrue();
    }

    [Fact]
    public async Task A_tampered_delivery_is_refused_with_401_and_enqueues_nothing()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body).Replace("1.50", "1.5"));

        (await StatusOf(receiver.ReceiveAsync("test", Request(tampered, Sign(Body)), default))).Should().Be(401);
        (await StatusOf(receiver.ReceiveAsync("test", Request(Body, signature: null), default))).Should().Be(401);

        (await QueuedAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_without_a_secret_refuses_everything()
    {
        await using var services = BuildServices(source => source.Verification = _ => new HmacSignatureStrategy(Scheme, ""));
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();

        (await StatusOf(receiver.ReceiveAsync("test", Request(Body, Sign(Body)), default))).Should().Be(401);
        (await QueuedAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_delivery_without_an_id_is_enqueued_each_time()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();

        await receiver.ReceiveAsync("test", Request(Body, Sign(Body), deliveryId: null), default);
        await receiver.ReceiveAsync("test", Request(Body, Sign(Body), deliveryId: null), default);

        (await QueuedAsync()).Count.Should().Be(2, "with no id there is nothing to deduplicate on; the handler must be idempotent");
    }

    [Fact]
    public async Task Cookie_and_Authorization_are_never_stored()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();

        await receiver.ReceiveAsync("test", Request(Body, Sign(Body), "delivery-1", ("Cookie", "session=s3cr3t"), ("Authorization", "Bearer t0k3n")), default);

        var payload = (await QueuedAsync()).Should().ContainSingle().Which.PayloadJson!;
        payload.Should().NotContain("s3cr3t");
        payload.Should().NotContain("t0k3n");
    }

    [Fact]
    public async Task A_content_type_the_source_does_not_accept_is_refused_with_415()
    {
        await using var services = BuildServices(source => source.ContentTypes.Add("application/json"));
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();
        var form = new WebhookRequest(
            [new("Content-Type", "application/x-www-form-urlencoded"), new("X-Test-Signature", Sign(Body))], Body);

        (await StatusOf(receiver.ReceiveAsync("test", form, default))).Should().Be(415);
        (await StatusOf(receiver.ReceiveAsync("test", Request(Body, Sign(Body)), default))).Should().Be(200,
            "parameters such as charset do not matter, and application/json is accepted");
    }

    [Fact]
    public async Task Reading_refuses_an_unknown_source_and_an_oversized_body_before_handling()
    {
        await using var services = BuildServices(source => source.MaxBodyBytes = 16);
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();
        var headers = new Dictionary<string, StringValues>();

        var unknown = await ThrowsAsync<EndpointBindingException>(async () =>
            await receiver.ReadAsync("nope", WebhookRoute.Shared, headers, new MemoryStream(Body), Body.Length, default));
        unknown.StatusCode.Should().Be(404);

        var ownRouteOnly = await ThrowsAsync<EndpointBindingException>(async () =>
            await receiver.ReadAsync("test", WebhookRoute.Own, headers, new MemoryStream(Body), Body.Length, default));
        ownRouteOnly.StatusCode.Should().Be(404, "a shared-route source is not served by a package's own route");

        var declared = await ThrowsAsync<EndpointBindingException>(async () =>
            await receiver.ReadAsync("test", WebhookRoute.Shared, headers, new MemoryStream(Body), Body.Length, default));
        declared.StatusCode.Should().Be(413);

        // A chunked body declares no length: the running total is what refuses it.
        var chunked = await ThrowsAsync<EndpointBindingException>(async () =>
            await receiver.ReadAsync("test", WebhookRoute.Shared, headers, new MemoryStream(Body), contentLength: null, default));
        chunked.StatusCode.Should().Be(413);
    }

    [Fact]
    public async Task Reading_keeps_the_bytes_exactly()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();
        // Not even valid UTF-8: the reader must not decode.
        byte[] raw = [0xEF, 0xBB, 0xBF, (byte)'{', 0xFF, 0xFE, (byte)'}'];

        var request = await receiver.ReadAsync("test", WebhookRoute.Shared, new Dictionary<string, StringValues>(), new MemoryStream(raw), null, default);

        request.Body.Should().Equal(raw);
    }

    [Fact]
    public async Task The_queued_delivery_reaches_the_sources_handler()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var recipient = scope.ServiceProvider.GetServices<IRecipient<WebhookDelivery>>().Should().ContainSingle().Which;
        var delivery = new WebhookDelivery { Source = "test", Body = Body };

        await recipient.HandleAsync(delivery);

        services.GetRequiredService<RecordingHandler.Log>().Handled.Should().ContainSingle().Which.Should().BeSameAs(delivery);
    }

    [Fact]
    public async Task A_queued_delivery_for_an_unregistered_source_is_not_retried()
    {
        await using var services = BuildServices();
        await using var scope = services.CreateAsyncScope();
        var recipient = scope.ServiceProvider.GetServices<IRecipient<WebhookDelivery>>().Should().ContainSingle().Which;

        await ThrowsAsync<NonRetryableException>(() => recipient.HandleAsync(new WebhookDelivery { Source = "gone" }));
    }

    [Fact]
    public async Task An_inline_source_runs_its_handler_in_the_request_and_enqueues_nothing()
    {
        // Its own host, with no message bus at all: an inline source never touches one.
        var builder = new SparkBuilder(new ServiceCollection());
        builder.AddWebhookSource("inline", source =>
        {
            source.Verification = _ => new HmacSignatureStrategy(Scheme, Secret);
            source.UseHandler<RecordingHandler>(WebhookHandling.Inline);
        });
        builder.Services.AddLogging();
        builder.Services.AddSingleton<RecordingHandler.Log>();
        await using var inlineServices = builder.Services.BuildServiceProvider();
        await using var scope = inlineServices.CreateAsyncScope();
        var receiver = scope.ServiceProvider.GetRequiredService<IWebhookReceiver>();

        (await StatusOf(receiver.ReceiveAsync("inline", Request(Body, Sign(Body)), default))).Should().Be(200);

        inlineServices.GetRequiredService<RecordingHandler.Log>().Handled.Should().ContainSingle();
        inlineServices.GetServices<IRecipient<WebhookDelivery>>().Should().BeEmpty("an inline-only app gets no spark-webhooks queue");
        (await QueuedAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("has space")]
    [InlineData("")]
    public void A_source_name_must_be_route_safe(string name)
    {
        var builder = new SparkBuilder(new ServiceCollection());

        Throws<ArgumentException>(() => builder.AddWebhookSource(name, source =>
        {
            source.Verification = _ => new FetchByIdStrategy();
            source.UseHandler<RecordingHandler>();
        }));
    }

    [Fact]
    public void A_source_without_verification_or_handler_is_refused_at_registration()
    {
        var builder = new SparkBuilder(new ServiceCollection());

        Throws<InvalidOperationException>(() => builder.AddWebhookSource("a", source => source.UseHandler<RecordingHandler>()));
        Throws<InvalidOperationException>(() => builder.AddWebhookSource("b", source => source.Verification = _ => new FetchByIdStrategy()));
        Throws<InvalidOperationException>(() => builder.AddWebhookSource("c", source =>
        {
            source.Verification = _ => new FetchByIdStrategy();
            source.UseHandler<RecordingHandler>();
            source.UseHandler<RecordingHandler>();
        }));
    }

    [Fact]
    public void A_source_name_is_registered_once()
    {
        var builder = new SparkBuilder(new ServiceCollection());
        void Add() => builder.AddWebhookSource("twice", source =>
        {
            source.Verification = _ => new FetchByIdStrategy();
            source.UseHandler<RecordingHandler>();
        });

        Add();
        Throws<InvalidOperationException>(Add);
    }

    internal sealed class RecordingHandler(RecordingHandler.Log log) : IWebhookHandler
    {
        public Task HandleAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
        {
            log.Handled.Add(delivery);
            return Task.CompletedTask;
        }

        internal sealed class Log
        {
            public List<WebhookDelivery> Handled { get; } = [];
        }
    }
}
