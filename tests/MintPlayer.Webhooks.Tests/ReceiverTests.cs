using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Webhooks;

namespace MintPlayer.Webhooks.Tests;

public class ReceiverTests
{
    private static readonly WebhookSecret Secret = WebhookSecret.Generate();

    public sealed record ReportCompleted(string ReportId, decimal LineRate);

    public sealed class RecordingProcessor : WebhookProcessor
    {
        public List<(string Type, string Id)> Seen { get; } = [];
        public ReportCompleted? Last { get; private set; }
        public bool Pinged { get; private set; }

        protected override Task DispatchAsync(WebhookContext context, CancellationToken ct)
        {
            if (context.Type == "report.completed") Last = Deserialize<ReportCompleted>(context);
            return base.DispatchAsync(context, ct);
        }

        protected override Task OnEventAsync(WebhookContext context, CancellationToken ct)
        {
            Seen.Add((context.Type, context.MessageId));
            if (context.Type == "boom") throw new InvalidOperationException("handler failed");
            return Task.CompletedTask;
        }

        protected override Task OnPingAsync(WebhookContext context, CancellationToken ct)
        {
            Pinged = true;
            return Task.CompletedTask;
        }
    }

    private static (Func<string, string?> Headers, byte[] Body) Signed(string type, object data, string id = "msg_1", WebhookSecret? secret = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type, timestamp = DateTimeOffset.UtcNow, data }, WebhookJson.Options);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = WebhookSignatureHeader.Create([new HmacWebhookSigner(secret ?? Secret)], new WebhookSigningInput(id, ts, body));
        return (n => n switch
        {
            WebhookHeaders.Id => id,
            WebhookHeaders.Timestamp => ts.ToString(),
            WebhookHeaders.Signature => signature,
            _ => null,
        }, body);
    }

    private static WebhookReceiver Receiver() => new(new WebhookVerifierOptions { Secrets = { Secret.ToString() } });

    [Fact]
    public async Task Verified_event_is_dispatched_with_typed_data()
    {
        var processor = new RecordingProcessor();
        var (headers, body) = Signed("report.completed", new ReportCompleted("r1", 0.875m));

        var result = await Receiver().HandleAsync(headers, body, processor);

        result.StatusCode.Should().Be(204, "a handled event is acknowledged with a 2xx");
        processor.Last.Should().Be(new ReportCompleted("r1", 0.875m), "data must round-trip through the shared wire options");
        processor.Seen.Should().ContainSingle("the untyped hook still sees the event after the typed one");
    }

    [Fact]
    public async Task Ping_goes_to_OnPing_not_to_the_event_handlers()
    {
        var processor = new RecordingProcessor();
        var (headers, body) = Signed(WebhookProcessor.PingEventType, new { });

        (await Receiver().HandleAsync(headers, body, processor)).StatusCode.Should().Be(204, "a ping is acknowledged");
        processor.Pinged.Should().BeTrue("the producer's ping must reach OnPingAsync");
        processor.Seen.Should().BeEmpty("a ping is not a business event");
    }

    [Fact]
    public async Task Bad_signature_is_401_and_never_dispatched()
    {
        var processor = new RecordingProcessor();
        var (headers, body) = Signed("report.completed", new ReportCompleted("r1", 1m), secret: WebhookSecret.Generate());

        var result = await Receiver().HandleAsync(headers, body, processor);

        result.StatusCode.Should().Be(401, "an unverified request is unauthenticated");
        processor.Seen.Should().BeEmpty("nothing unverified may reach a handler");
    }

    [Fact]
    public async Task Signed_body_that_is_not_an_envelope_is_400()
    {
        var body = Encoding.UTF8.GetBytes("[1,2,3]");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = WebhookSignatureHeader.Create([new HmacWebhookSigner(Secret)], new WebhookSigningInput("msg_x", ts, body));
        Func<string, string?> headers = n => n == WebhookHeaders.Id ? "msg_x" : n == WebhookHeaders.Timestamp ? ts.ToString() : n == WebhookHeaders.Signature ? sig : null;

        (await Receiver().HandleAsync(headers, body, new RecordingProcessor())).StatusCode.Should().Be(400, "a Standard Webhooks body is {type,timestamp,data}");
    }

    private static async Task<(TestServer Server, HttpClient Client)> HostAsync(long maxBody = 1024 * 1024)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWebhookReceiver<RecordingProcessor>(o =>
        {
            o.Secrets.Add(Secret.ToString());
            o.MaxBodyBytes = maxBody;
        });
        builder.Services.AddSingleton<List<string>>();
        var app = builder.Build();
        app.MapWebhookReceiver<RecordingProcessor>("/hooks");
        await app.StartAsync();
        var server = app.GetTestServer();
        return (server, server.CreateClient());
    }

    private static HttpRequestMessage Request(Func<string, string?> headers, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/hooks") { Content = new ByteArrayContent(body) };
        foreach (var name in new[] { WebhookHeaders.Id, WebhookHeaders.Timestamp, WebhookHeaders.Signature })
            request.Headers.TryAddWithoutValidation(name, headers(name));
        return request;
    }

    [Fact]
    public async Task Mapped_endpoint_answers_204_401_413_and_500()
    {
        var (_, client) = await HostAsync(maxBody: 4096);

        var (h1, b1) = Signed("report.completed", new ReportCompleted("r1", 1m));
        (await client.SendAsync(Request(h1, b1))).StatusCode.Should().Be(HttpStatusCode.NoContent, "a verified event is handled");

        var (h2, b2) = Signed("report.completed", new ReportCompleted("r1", 1m), secret: WebhookSecret.Generate());
        (await client.SendAsync(Request(h2, b2))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a bad signature is refused");

        var (h3, b3) = Signed("report.completed", new { blob = new string('x', 5000) });
        (await client.SendAsync(Request(h3, b3))).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, "bodies over MaxBodyBytes are refused before verification");

        var (h4, b4) = Signed("boom", new { });
        var failed = async () => await client.SendAsync(Request(h4, b4));
        // TestServer rethrows unhandled exceptions to the client by default; a real host answers 500. Either way it is not a 2xx.
        var outcome = await Record.ExceptionAsync(failed);
        if (outcome is null)
            (await client.SendAsync(Request(h4, b4))).IsSuccessStatusCode.Should().BeFalse("a failing handler must make the producer retry");
        else
            outcome.Should().BeOfType<InvalidOperationException>("the handler's exception surfaces, so the producer sees a failure");
    }
}
