using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Webhooks.GitHub.Endpoints;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using System.Net.WebSockets;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// The dev WebSocket behind Spark's real pipeline (<c>UseSpark()</c>): the same-origin guard on
/// WebSocket upgrades (R2-H5, <c>SparkMiddleware</c>) refuses a cross-origin handshake before the
/// generator endpoint runs, exactly as it did for the hand-mapped route it replaced.
/// </summary>
public class DevWebSocketOriginGuardTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private SparkEndpointFactory<TestSparkContext> Factory() => new(
        Store,
        models: [],
        configureSpark: spark => spark.AddMessaging().AddGithubWebhooks(o =>
        {
            o.WebhookSecret = "origin-guard";
            o.DevelopmentAppId = 1;
        }));

    private static Task<WebSocket> ConnectAsync(SparkEndpointFactory<TestSparkContext> factory, string? origin)
    {
        var client = ((TestServer)factory.GetService<IServer>()).CreateWebSocketClient();
        client.SubProtocols.Add("wss");
        if (origin is not null)
            client.ConfigureRequest = request => request.Headers.Origin = origin;
        return client.ConnectAsync(new Uri("ws://localhost" + DevWebSocketEndpoint.Path), CancellationToken.None);
    }

    [Fact]
    public async Task A_cross_origin_upgrade_is_refused_with_403()
    {
        await using var factory = Factory();

        var connect = () => ConnectAsync(factory, "https://attacker.example");

        await connect.Should().ThrowAsync<InvalidOperationException>().WithMessage("*403*");
    }

    /// <summary>The control: the same host accepts the upgrade without an Origin, so the 403 above is the guard's.</summary>
    [Fact]
    public async Task An_upgrade_without_an_origin_reaches_the_endpoint()
    {
        await using var factory = Factory();

        using var ws = await ConnectAsync(factory, origin: null);

        ws.State.Should().Be(WebSocketState.Open);
        ws.SubProtocol.Should().Be("wss");
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
