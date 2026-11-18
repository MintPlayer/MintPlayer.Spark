using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Cli.Services;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.Receiver;
using Generated = MintPlayer.Spark.Tests.Apps.Generated;

namespace MintPlayer.Spark.Tests.Apps;

public enum BuildOutcome { Succeeded, Failed, TimedOut }

public sealed record BuildCommit(string Sha, [property: Description("The first line of the message.")] string? Message);

public sealed record BuildStep(string Name, long DurationMs, IReadOnlyList<BuildStep> Children);

/// <summary>A richer event than the shop's two: an enum, nested and recursive types, a list, a map, a Guid, a nullable reference.</summary>
[WebhookEvent("build.finished", Permission = "builds:read", Description = "A build finished.")]
public sealed record BuildFinished(
    string BuildId,
    BuildOutcome Outcome,
    BuildOutcome? PreviousOutcome,
    BuildCommit Commit,
    IReadOnlyList<BuildStep> Steps,
    Dictionary<string, decimal> Coverage,
    Guid RunId,
    BuildStep? Slowest);

/// <summary>
/// #691 M8 (<c>docs/apps_webhooks_PRD.md</c>): a Spark site sends, `spark webhooks generate` turns its catalog into
/// classes, and a Spark app receives them typed through MintPlayer.Spark.Webhooks.Receiver. The site and the receiver
/// are one host here; the delivery leaves through the SSRF-safe client and comes back in over HTTP.
/// </summary>
public class WebhookReceiverRoundTripTests : AppsTestHost
{
    private const string ReceiverPath = "/webhooks/shop";

    private readonly Recorder<Generated.OrderShipped> shipped = new();
    private readonly Recorder<Generated.BuildFinished> builds = new();
    private string secret = "";

    protected override void ConfigureAppsBuilder(AppsBuilder apps) => apps.AddEvent<BuildFinished>();

    protected override void ConfigureSpark(ISparkBuilder spark)
    {
        // The options are read on the first delivery, after the test has created the endpoint and knows its secret.
        spark.AddWebhookReceiver(ReceiverPath, Generated.ShopWebhooksProcessor.EventTypes, o => o.Secrets.Add(secret));
        spark.Services.AddSingleton<IRecipient<WebhookMessage<Generated.OrderShipped>>>(shipped);
        spark.Services.AddSingleton<IRecipient<WebhookMessage<Generated.BuildFinished>>>(builds);
    }

    [Fact]
    public async Task The_generated_code_is_what_the_CLI_makes_of_this_catalog()
    {
        var (status, catalog) = await GetAsync("/spark/apps/webhooks/catalog", null);
        status.Should().Be(HttpStatusCode.OK, "codegen reads the catalog anonymously");

        var cs = WebhookGenerator.Generate(catalog.GetRawText(), "cs", "Shop", "MintPlayer.Spark.Tests.Apps.Generated").Replace("\r\n", "\n");
        var ts = WebhookGenerator.Generate(catalog.GetRawText(), "ts", "Shop", null).Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("SPARK_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(RepoFile("tests", "MintPlayer.Spark.Tests", "Apps", "Generated", "ShopWebhooks.g.cs"), cs);
            File.WriteAllText(RepoFile("libs", "node_packages", "webhooks-receiver", "src", "testing", "shop-webhooks.ts"), ts);
        }

        cs.Should().Be(Read("tests", "MintPlayer.Spark.Tests", "Apps", "Generated", "ShopWebhooks.g.cs"),
            "the compiled ShopWebhooks.g.cs is the CLI's output for this catalog (SPARK_UPDATE_GOLDEN=1 rewrites it)");
        ts.Should().Be(Read("libs", "node_packages", "webhooks-receiver", "src", "testing", "shop-webhooks.ts"),
            "the npm package's specs type-check against the CLI's TypeScript for this catalog (SPARK_UPDATE_GOLDEN=1 rewrites it)");
    }

    [Fact]
    public async Task A_sent_event_arrives_typed_once_even_when_it_is_redelivered()
    {
        var ann = await SeedUserAsync("ann");
        var (endpointId, endpointSecret) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        secret = endpointSecret;
        Receiver.Forward = ForwardToReceiverAsync;

        var messageId = await PublishAsync(new OrderShipped("o-1", 3, "DHL"), WebhookOwner.User(ann.Id!));
        await Until(() => Task.FromResult(!shipped.Received.IsEmpty), "the recipient got order.shipped");

        var message = shipped.Received.Single();
        message.MessageId.Should().Be(messageId, "the message carries the producer's webhook-id");
        message.Type.Should().Be("order.shipped");
        message.Receiver.Should().Be(ReceiverPath);
        message.Data.OrderId.Should().Be("o-1");
        message.Data.Items.Should().Be(3);
        message.Data.Carrier.Should().Be("DHL");

        await UntilSettledAsync(endpointId, 1);
        var original = (await DeliveriesAsync(endpointId)).Single();
        original.Attempts.Single().StatusCode.Should().Be(204, "the receiver answers once the event is enqueued");

        var (status, _) = await SendAsync("/spark/apps/webhooks/deliveries/redeliver", new { id = original.PublicId }, ann.Id);
        status.Should().Be(HttpStatusCode.OK);
        await UntilSettledAsync(endpointId, 2);

        // The redelivery was answered 204 after BroadcastOnce found the webhook-id already enqueued: nothing new to run.
        (await DeliveriesAsync(endpointId)).Should().OnlyContain(d => d.Attempts.Last().StatusCode == 204, "both deliveries were accepted");
        shipped.Received.Should().HaveCount(1, "the receiver deduplicates on webhook-id, so a redelivery is not handled twice");
    }

    [Fact]
    public async Task Nested_enum_recursive_and_map_data_arrive_intact()
    {
        var ann = await SeedUserAsync("ann");
        var (_, endpointSecret) = await CreateEndpointAsync(ann.Id!, WebhookOwner.User(ann.Id!));
        secret = endpointSecret;
        Receiver.Forward = ForwardToReceiverAsync;
        var runId = Guid.NewGuid();
        var leaf = new BuildStep("test", 900, []);

        await PublishAsync(new BuildFinished("b-7", BuildOutcome.TimedOut, null, new BuildCommit("abc123", null),
            [new BuildStep("build", 1200, [leaf])], new() { ["src"] = 81.25m }, runId, leaf), WebhookOwner.User(ann.Id!));
        await Until(() => Task.FromResult(!builds.Received.IsEmpty), "the recipient got build.finished");

        var data = builds.Received.Single().Data;
        data.Outcome.Should().Be(Generated.BuildOutcome.TimedOut, "string enums round-trip by name");
        data.PreviousOutcome.HasValue.Should().BeFalse("a null enum stays null");
        data.Commit.Sha.Should().Be("abc123");
        data.Steps.Single().Children.Single().Name.Should().Be("test", "a recursive type is one generated class");
        data.Coverage["src"].Should().Be(81.25m);
        data.RunId.Should().Be(runId);
        data.Slowest!.DurationMs.Should().Be(900);
    }

    [Fact]
    public async Task A_wrong_signature_is_refused_and_an_unknown_event_without_a_recipient_is_dropped()
    {
        secret = "whsec_" + Convert.ToBase64String(new byte[32]);
        using var client = Factory.CreateClient();
        var body = """{"type":"order.shipped","timestamp":"2026-11-18T10:00:00Z","data":{"orderId":"o","items":1,"carrier":null}}"""u8.ToArray();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var forged = Request(body, "msg_forged", now, "v1,AAAA");
        (await client.SendAsync(forged)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the signature is the authentication");

        var other = """{"type":"shop.closed","timestamp":"2026-11-18T10:00:00Z","data":{}}"""u8.ToArray();
        var signed = Request(other, "msg_unknown", now, Sign(other, "msg_unknown", now));
        (await client.SendAsync(signed)).StatusCode.Should().Be(HttpStatusCode.NoContent, "an event this app does not know is acknowledged, not retried forever");
        shipped.Received.Should().BeEmpty();
    }

    private async Task<HttpResponseMessage> ForwardToReceiverAsync(FakeReceiver.Request request)
    {
        using var client = Factory.CreateClient();
        return await client.SendAsync(Request(request.Body, request.Header("webhook-id"), long.Parse(request.Header("webhook-timestamp")), request.Header("webhook-signature")));
    }

    private static HttpRequestMessage Request(byte[] body, string id, long timestamp, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ReceiverPath) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("webhook-signature", signature);
        return request;
    }

    private string Sign(byte[] body, string id, long timestamp)
    {
        var key = MintPlayer.Webhooks.WebhookSecret.Parse(secret).GetKey();
        using var hmac = new System.Security.Cryptography.HMACSHA256(key);
        var signature = hmac.ComputeHash([.. Encoding.UTF8.GetBytes($"{id}.{timestamp}."), .. body]);
        return "v1," + Convert.ToBase64String(signature);
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepoFile(parts)).Replace("\r\n", "\n");

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
            directory = directory.Parent;
        return directory is null
            ? throw new InvalidOperationException($"Could not locate the repository root above '{AppContext.BaseDirectory}'.")
            : Path.Combine([directory.FullName, .. parts]);
    }

    internal sealed class Recorder<T> : IRecipient<WebhookMessage<T>>
    {
        public ConcurrentQueue<WebhookMessage<T>> Received { get; } = new();

        public Task HandleAsync(WebhookMessage<T> message, CancellationToken cancellationToken = default)
        {
            Received.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
