using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Apps.Models;
using MintPlayer.Spark.Organizations;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Tests.Apps;

/// <summary>
/// #691 M2/M3 (<c>docs/apps_webhooks_PRD.md</c>): the event catalog, direct webhooks and the delivery engine, end to
/// end through Messaging and the package's SSRF-safe client (with a fake receiver behind it).
/// </summary>
public class WebhookDeliveryTests : AppsTestHost
{
    [Fact]
    public async Task The_catalog_is_an_OpenAPI_webhooks_document_of_the_declared_events_and_anonymous()
    {
        var (status, body) = await GetAsync("/spark/apps/webhooks/catalog", null);

        status.Should().Be(HttpStatusCode.OK, "codegen reads the catalog without signing in");
        var actual = JsonNode.Parse(body.GetRawText())!;
        var expected = JsonNode.Parse("""
            {
              "openapi": "3.1.0",
              "info": { "title": "Shop webhooks", "version": "1.0.0" },
              "webhooks": {
                "invoice.paid": {
                  "post": {
                    "operationId": "invoice.paid",
                    "summary": "invoice.paid",
                    "requestBody": {
                      "required": true,
                      "content": { "application/json": { "schema": {
                        "type": "object",
                        "required": [ "type", "timestamp", "data" ],
                        "properties": {
                          "type": { "const": "invoice.paid" },
                          "timestamp": { "type": "string", "format": "date-time" },
                          "data": {
                            "title": "InvoicePaid",
                            "type": "object",
                            "properties": {
                              "invoiceId": { "type": "string" },
                              "amount": { "type": "number" },
                              "paidAt": { "type": "string", "format": "date-time" }
                            },
                            "required": [ "invoiceId", "amount", "paidAt" ]
                          }
                        }
                      } } }
                    },
                    "responses": { "2XX": { "description": "Received." } }
                  }
                },
                "order.shipped": {
                  "post": {
                    "operationId": "order.shipped",
                    "summary": "An order left the warehouse.",
                    "requestBody": {
                      "required": true,
                      "content": { "application/json": { "schema": {
                        "type": "object",
                        "required": [ "type", "timestamp", "data" ],
                        "properties": {
                          "type": { "const": "order.shipped" },
                          "timestamp": { "type": "string", "format": "date-time" },
                          "data": {
                            "title": "OrderShipped",
                            "type": "object",
                            "properties": {
                              "orderId": { "type": "string" },
                              "items": { "type": "integer" },
                              "carrier": { "type": [ "string", "null" ], "description": "The carrier's name." }
                            },
                            "required": [ "orderId", "items", "carrier" ]
                          }
                        }
                      } } }
                    },
                    "responses": { "2XX": { "description": "Received." } },
                    "x-spark-permission": "orders:read"
                  }
                }
              },
              "x-spark-permissions": [ "orders:read" ]
            }
            """);
        JsonNode.DeepEquals(actual, expected).Should().BeTrue("the catalog holds the two sample events exactly:\n" + actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task A_published_event_reaches_the_endpoint_signed_with_its_secret()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, secret) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        var messageId = await PublishAsync(new OrderShipped("o-1", 3, "DHL"), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        var request = Receiver.Received.Single();
        request.Url.Should().Be(ReceiverUrl);
        request.Header("webhook-id").Should().Be(messageId, "the publish returns the event's webhook-id");
        var verified = new WebhookVerifier(new WebhookVerifierOptions { Secrets = { secret } }).Verify(h => request.Header(h), request.Body);
        verified.IsValid.Should().BeTrue("the receiver verifies with the secret it was shown: " + verified.Failure);
        request.Json.GetProperty("type").GetString().Should().Be("order.shipped");
        request.Json.GetProperty("data").GetProperty("orderId").GetString().Should().Be("o-1");
        request.Json.GetProperty("data").GetProperty("items").GetInt32().Should().Be(3);

        var (status, log) = await GetAsync($"/spark/apps/webhooks/deliveries?endpointId={endpointId}", ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        var entry = log.EnumerateArray().Single();
        entry.GetProperty("status").GetString().Should().Be("Succeeded");
        entry.GetProperty("attempts").GetArrayLength().Should().Be(1);
        entry.GetProperty("attempts")[0].GetProperty("statusCode").GetInt32().Should().Be(204);
    }

    [Fact]
    public async Task A_rolled_back_write_sends_nothing_and_an_owner_without_webhooks_costs_no_message()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        using (var scope = Factory.CreateScope())
        using (var session = Store.OpenAsyncSession())
        {
            (await Publisher(scope).PublishAsync(new OrderShipped("rolled-back", 1, null), WebhookOwner.User(ann.Id!), session)).Should().NotBeNull();
            // no SaveChanges: the business write failed, so the event never happened
        }
        (await PublishAsync(new OrderShipped("bob's", 1, null), WebhookOwner.User(bob.Id!))).Should().BeNull("bob has no webhook");

        await PublishAsync(new OrderShipped("committed", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);
        Receiver.Received.Select(r => r.Json.GetProperty("data").GetProperty("orderId").GetString()).Should().BeEquivalentTo(["committed"]);
    }

    [Fact]
    public async Task A_keyed_publish_that_runs_twice_queues_the_event_once()
    {
        var ann = await SeedUserAsync("ann");
        var owner = WebhookOwner.User(ann.Id!);
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, owner);

        async Task<string?> Keyed(string orderId, string key)
        {
            using var scope = Factory.CreateScope();
            using var session = Store.OpenAsyncSession();
            var id = await Publisher(scope).PublishAsync(new OrderShipped(orderId, 1, null), owner, key, session);
            await session.SaveChangesAsync();
            return id;
        }

        var first = await Keyed("o-1", "build-1");
        await Task.Delay(20); // a later run: the publish time differs, so only the key can match it
        var rerun = await Keyed("o-1 again", "build-1");
        var other = await Keyed("o-2", "build-2");

        rerun.Should().Be(first, "the webhook-id derives from the key, not the moment of publishing");
        other.Should().NotBe(first, "another key is another event");
        await UntilSettledAsync(endpointId, 2);
        // The rerun queued nothing; the first run's payload stands.
        (await DeliveriesAsync(endpointId)).Select(d => d.MessageId).Should().BeEquivalentTo([first, other]);
        Receiver.Received.Select(r => r.Json.GetProperty("data").GetProperty("orderId").GetString()).Should().BeEquivalentTo(["o-1", "o-2"]);
    }

    [Fact]
    public async Task An_endpoint_receives_only_the_events_it_subscribed_to()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!), ["invoice.paid"]);

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await PublishAsync(new InvoicePaid("i-1", 12.5m, DateTimeOffset.UtcNow), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        (await DeliveriesAsync(endpointId)).Select(d => d.EventType).Should().BeEquivalentTo(["invoice.paid"]);
    }

    [Fact]
    public async Task A_failed_attempt_is_retried_with_the_same_webhook_id_and_the_log_keeps_both()
    {
        var ann = await SeedUserAsync("ann");
        Receiver.Respond = (_, n) => n == 0 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") } : null;
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        Receiver.Received.Select(r => r.Header("webhook-id")).Distinct().Should().HaveCount(1, "a retry is the same message");
        var delivery = (await DeliveriesAsync(endpointId)).Single();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Succeeded);
        delivery.Attempts.Select(a => a.StatusCode).Should().Equal([500, 204]);
        delivery.Attempts[0].ResponseBody.Should().Be("boom");
        (await EndpointAsync(endpointId)).ConsecutiveFailures.Should().Be(0, "a success ends the failure streak");
    }

    [Fact]
    public async Task Running_out_of_attempts_marks_the_delivery_failed()
    {
        var ann = await SeedUserAsync("ann");
        Receiver.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway);
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        var delivery = (await DeliveriesAsync(endpointId)).Single();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);
        Receiver.Received.Count.Should().Be(3, "the test queue allows 3 attempts");
        var endpoint = await EndpointAsync(endpointId);
        endpoint.Enabled.Should().BeTrue("one failed event does not disable an endpoint");
        endpoint.FailingSinceUtc.HasValue.Should().BeTrue("the failure streak started");
    }

    [Fact]
    public async Task Gone_disables_the_endpoint_tells_the_handler_and_later_events_skip_it()
    {
        var ann = await SeedUserAsync("ann");
        Receiver.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Gone);
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        Receiver.Received.Count.Should().Be(1, "410 is never retried");
        var endpoint = await EndpointAsync(endpointId);
        endpoint.Enabled.Should().BeFalse();
        endpoint.DisabledReason.Should().Contain("410");
        await UntilDisabledHandlerToldAsync(endpointId);
        DisabledCount(endpointId).Should().Be(1);

        await PublishAsync(new OrderShipped("o-2", 1, null), WebhookOwner.User(ann.Id!));
        await Task.Delay(1500);
        (await DeliveriesAsync(endpointId)).Should().HaveCount(1, "a disabled endpoint gets no new deliveries");
    }

    [Fact]
    public async Task Retry_After_waits_as_asked_without_spending_an_attempt()
    {
        var ann = await SeedUserAsync("ann");
        Receiver.Respond = (_, n) => n == 0
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2)) } }
            : null;
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        var requests = Receiver.Received.ToList();
        requests.Should().HaveCount(2);
        (requests[1].AtUtc - requests[0].AtUtc).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9), "the receiver asked for 2 s");
        (await DeliveriesAsync(endpointId)).Single().Status.Should().Be(WebhookDeliveryStatus.Succeeded);
    }

    [Fact]
    public async Task Redelivery_sends_the_same_payload_and_webhook_id_and_links_the_original()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);
        var original = (await DeliveriesAsync(endpointId)).Single();

        var (status, body) = await SendAsync("/spark/apps/webhooks/deliveries/redeliver", new { id = original.PublicId }, ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        await UntilSettledAsync(endpointId, 2);

        var requests = Receiver.Received.ToList();
        requests.Should().HaveCount(2);
        requests[1].Header("webhook-id").Should().Be(requests[0].Header("webhook-id"), "a receiver recognises a redelivery by its id");
        Encoding.UTF8.GetString(requests[1].Body).Should().Be(Encoding.UTF8.GetString(requests[0].Body));
        var copy = (await DeliveriesAsync(endpointId)).Single(d => d.PublicId == body.GetProperty("id").GetString());
        copy.RedeliveryOf.Should().Be(original.PublicId);

        var (logStatus, log) = await GetAsync($"/spark/apps/webhooks/deliveries?endpointId={endpointId}", ann.Id);
        logStatus.Should().Be(HttpStatusCode.OK);
        log[0].GetProperty("id").GetString().Should().Be(copy.PublicId, "the log lists the newest first");
    }

    [Fact]
    public async Task After_a_rotation_deliveries_carry_both_signatures_until_the_overlap_ends()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, oldSecret) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        var (status, body) = await SendAsync("/spark/apps/webhooks/endpoints/rotate-secret", new { id = endpointId }, ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        var newSecret = body.GetProperty("secret").GetString()!;
        newSecret.Should().NotBe(oldSecret);

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        var request = Receiver.Received.Single();
        foreach (var secret in new[] { oldSecret, newSecret })
            new WebhookVerifier(new WebhookVerifierOptions { Secrets = { secret } }).Verify(h => request.Header(h), request.Body).IsValid
                .Should().BeTrue("a receiver on either secret verifies during the overlap");
        WebhookSignatureHeader.Parse(request.Header("webhook-signature")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Ping_reaches_the_endpoint_whatever_it_subscribes_to()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, secret) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!), ["invoice.paid"]);

        var (status, _) = await SendAsync("/spark/apps/webhooks/endpoints/ping", new { id = endpointId }, ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        await UntilSettledAsync(endpointId, 1);

        var request = Receiver.Received.Single();
        request.Json.GetProperty("type").GetString().Should().Be("ping");
        request.Json.GetProperty("data").GetProperty("endpointId").GetString().Should().Be(endpointId);
        new WebhookVerifier(new WebhookVerifierOptions { Secrets = { secret } }).Verify(h => request.Header(h), request.Body).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_payload_over_the_cap_goes_out_thin()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped(new string('x', 4000), 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        var data = Receiver.Received.Single().Json.GetProperty("data");
        data.GetProperty("truncated").GetBoolean().Should().BeTrue("the test cap is 2 KB");
        data.GetProperty("size").GetInt32().Should().BeGreaterThan(4000);
    }

    [Fact]
    public async Task An_endpoint_failing_for_the_whole_window_is_disabled()
    {
        var ann = await SeedUserAsync("ann");
        Receiver.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        using (var session = Store.OpenAsyncSession())
        {
            var endpoint = await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdPrefix + endpointId);
            endpoint!.FailingSinceUtc = DateTime.UtcNow.AddDays(-6);
            await session.SaveChangesAsync();
        }

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        Receiver.Received.Count.Should().Be(1, "the first failure past 5 days disables the endpoint; no retry follows");
        (await EndpointAsync(endpointId)).Enabled.Should().BeFalse();
        await UntilDisabledHandlerToldAsync(endpointId);
        DisabledCount(endpointId).Should().Be(1);

        Receiver.Respond = null;
        (await SendAsync("/spark/apps/webhooks/endpoints/enable", new { id = endpointId }, ann.Id)).Status.Should().Be(HttpStatusCode.OK);
        var enabled = await EndpointAsync(endpointId);
        enabled.Enabled.Should().BeTrue();
        enabled.FailingSinceUtc.HasValue.Should().BeFalse("enabling starts a new window");
    }

    [Fact]
    public async Task An_open_breaker_sends_nothing()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        using (var session = Store.OpenAsyncSession())
        {
            var endpoint = await session.LoadAsync<WebhookEndpoint>(WebhookEndpoint.IdPrefix + endpointId);
            endpoint!.ConsecutiveFailures = 3;
            endpoint.LastFailureUtc = DateTime.UtcNow;
            await session.SaveChangesAsync();
        }

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        Receiver.Received.Should().BeEmpty("three failures within the minute open the breaker");
        var delivery = (await DeliveriesAsync(endpointId)).Single();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);
        delivery.Attempts.Should().AllSatisfy(a => a.Error.Should().Contain("breaker"));
    }

    [Fact]
    public async Task A_destination_the_safe_client_refuses_fails_at_once_and_counts_against_the_endpoint()
    {
        var ann = await SeedUserAsync("ann");
        Receiver.Respond = (_, _) => throw new MintPlayer.Spark.Http.SafeHttpBlockedException(MintPlayer.Spark.Http.SafeHttpBlockReason.Address);
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        var delivery = (await DeliveriesAsync(endpointId)).Single();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);
        delivery.Attempts.Should().ContainSingle("a refused address is not retried").Which.Error.Should().Be("Refused: The target address is not allowed.");
        (await EndpointAsync(endpointId)).ConsecutiveFailures.Should().Be(1);
    }

    [Fact]
    public async Task A_network_error_is_a_failed_attempt_and_an_unreadable_answer_has_no_excerpt()
    {
        var ann = await SeedUserAsync("ann");
        Receiver.Respond = (_, n) => n switch
        {
            0 => throw new HttpRequestException("Connection reset."),
            // A 503 without Retry-After spends an attempt like any failure.
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new UnreadableContent() },
            _ => null,
        };
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        var delivery = (await DeliveriesAsync(endpointId)).Single();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Succeeded);
        delivery.Attempts.Select(a => a.StatusCode).Should().Equal([null, 503, 204]);
        delivery.Attempts[0].Error.Should().Be("Connection reset.");
        delivery.Attempts[1].ResponseBody.Should().BeNull("a body that cannot be read is no excerpt, not a failure");
    }

    [Fact]
    public async Task A_retry_for_an_endpoint_disabled_meanwhile_is_not_sent()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, _) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        Receiver.Respond = (_, _) =>
        {
            // Disabled between the first attempt and its retry.
            using var session = Store.OpenSession();
            session.Load<WebhookEndpoint>(WebhookEndpoint.IdPrefix + endpointId)!.Enabled = false;
            session.SaveChanges();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        };

        await PublishAsync(new OrderShipped("o-1", 1, null), WebhookOwner.User(ann.Id!));
        await UntilSettledAsync(endpointId, 1);

        Receiver.Received.Should().HaveCount(1, "the retry finds the endpoint disabled");
        var delivery = (await DeliveriesAsync(endpointId)).Single();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);
        delivery.Attempts.Last().Error.Should().Be("The endpoint is disabled.");
    }

    /// <summary>The headers arrive, then the connection drops while the body is read (the safe client opens the stream early).</summary>
    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new IOException("The connection dropped.");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new DroppingStream());
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class DroppingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection dropped.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("The connection dropped.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Only_the_owner_manages_a_webhook_and_secrets_are_never_listed()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var (endpointId, secret) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        var annsList = $"/spark/apps/webhooks/endpoints?ownerKind=user&ownerKey={Uri.EscapeDataString(ann.Id!)}";

        var (status, list) = await GetAsync(annsList, ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        list.GetRawText().Should().NotContain(secret).And.NotContain("sdp1:");
        (await EndpointAsync(endpointId)).ProtectedSecret.Should().StartWith("sdp1:").And.NotContain(secret, "the secret is encrypted at rest");

        (await GetAsync(annsList, bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync(annsList, null)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync("/spark/apps/webhooks/endpoints", new { ownerKind = "user", ownerKey = ann.Id, url = ReceiverUrl, events = new[] { "*" } }, bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        foreach (var action in new[] { "delete", "rotate-secret", "enable", "ping" })
            (await SendAsync($"/spark/apps/webhooks/endpoints/{action}", new { id = endpointId }, bob.Id)).Status.Should().Be(HttpStatusCode.NotFound, action + " on someone else's webhook");
        (await GetAsync($"/spark/apps/webhooks/deliveries?endpointId={endpointId}", bob.Id)).Status.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync("/spark/apps/webhooks/endpoints/delete", new { id = "../SparkUsers/ann" }, ann.Id)).Status.Should().Be(HttpStatusCode.NotFound, "an id of another shape is never loaded");

        (await SendAsync("/spark/apps/webhooks/endpoints/delete", new { id = endpointId }, ann.Id)).Status.Should().Be(HttpStatusCode.NoContent);
        (await GetAsync(annsList, ann.Id)).Body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Creating_refuses_bad_urls_and_unknown_events()
    {
        var ann = await SeedUserAsync("ann");
        async Task<HttpStatusCode> Create(string url, string[] events)
            => (await SendAsync("/spark/apps/webhooks/endpoints", new { ownerKind = "user", ownerKey = ann.Id, url, events }, ann.Id)).Status;

        (await Create("http://hooks.example.com/in", ["*"])).Should().Be(HttpStatusCode.BadRequest, "https only");
        (await Create("https://user:pw@hooks.example.com/in", ["*"])).Should().Be(HttpStatusCode.BadRequest, "no credentials in the URL");
        (await Create("not a url", ["*"])).Should().Be(HttpStatusCode.BadRequest);
        (await Create(ReceiverUrl, ["order.cancelled"])).Should().Be(HttpStatusCode.BadRequest, "not in the catalog");
        (await Create(ReceiverUrl, [])).Should().Be(HttpStatusCode.BadRequest);
        (await Create(ReceiverUrl, ["order.shipped", "ping"])).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Organization_admins_manage_its_webhooks_and_members_do_not()
    {
        var ann = await SeedUserAsync("ann");
        var bob = await SeedUserAsync("bob");
        var cid = await SeedUserAsync("cid");
        var org = await SeedOrganizationAsync("Acme", ann, (bob, OrganizationRole.Admin), (cid, OrganizationRole.Member));
        var owner = WebhookOwner.Organization(org.Id.Value);

        var (endpointId, _) = await CreateEndpointAsync(bob.Id!, owner);
        (await SendAsync("/spark/apps/webhooks/endpoints", new { ownerKind = owner.Kind, ownerKey = owner.Key, url = ReceiverUrl, events = new[] { "*" } }, cid.Id)).Status
            .Should().Be(HttpStatusCode.NotFound, "a member is not an admin");

        var (status, owners) = await GetAsync("/spark/apps/webhooks/owners", bob.Id);
        status.Should().Be(HttpStatusCode.OK);
        owners.EnumerateArray().Select(o => o.GetProperty("displayName").GetString()).Should().Contain("Acme");
        (await GetAsync("/spark/apps/webhooks/owners", cid.Id)).Body.EnumerateArray().Select(o => o.GetProperty("kind").GetString()).Should().NotContain("organization");

        await PublishAsync(new InvoicePaid("i-1", 10m, DateTimeOffset.UtcNow), owner);
        await UntilSettledAsync(endpointId, 1);
        Receiver.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task Publishing_an_undeclared_type_is_refused()
    {
        var ann = await SeedUserAsync("ann");
        using var scope = Factory.CreateScope();
        using var session = Store.OpenAsyncSession();
        var publish = () => Publisher(scope).PublishAsync(new { Hello = "world" }, WebhookOwner.User(ann.Id!), session);
        (await publish.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("not a webhook event");
    }
}
