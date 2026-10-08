using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Tests.Webhooks.GitHub._Infrastructure;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// The production end of the dev tunnel: the WebSocket endpoint that decides which developer may
/// receive webhook traffic. GitHub is a WireMock server reached through
/// <see cref="IGitHubClientFactory"/>, which is why the endpoint now builds its client there — it
/// used to call <c>new GitHubClient</c> inline, so nothing past the upgrade could be tested
/// without a real personal access token.
/// </summary>
public class DevWebSocketEndpointTests : IAsyncLifetime
{
    private const string Path = "/spark/github/dev-ws";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private WireMockServer _github = null!;
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        // WireMock's first request in a process scans every DLL in the output folder for a plugin
        // (0.9 s warm, 10-25 s on freshly built binaries). Paid here, outside the tests' 10 s bounds,
        // which are meant to bound the endpoint and not WireMock's start-up.
        await WireMockWarmUp.EnsureAsync();

        _github = WireMockServer.Start();

        var builder = new SparkBuilder(new ServiceCollection());
        builder.AddGithubWebhooks(o =>
        {
            o.WebhookSecret = "not-used-here";
            o.DevelopmentAppId = 1;
            o.AllowedDevUsers = ["alice"];
            o.DevSocketFilter = (_, _) => true;
        });

        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    foreach (var descriptor in builder.Services)
                        services.Add(descriptor);
                    services.AddRouting();
                    services.AddSingleton<IGitHubClientFactory>(new WireMockGitHubClientFactory(new Uri(_github.Url!)));
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => builder.Registry.MapEndpoints(endpoints));
                }))
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _github.Dispose();
    }

    /// <summary>GitHub's answer to <c>GET /user</c> for any token.</summary>
    private void GitHubAnswers(int status, string body)
        => _github
            .Given(Request.Create().WithPath("/api/v3/user").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(status)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));

    private async Task<WebSocket> ConnectAsync(string? token)
    {
        var client = _host.GetTestServer().CreateWebSocketClient();
        client.SubProtocols.Add("wss");
        var ws = await client.ConnectAsync(new Uri("ws://localhost" + Path), CancellationToken.None);
        await ws.WriteObject(new Handshake { GithubToken = token });
        return ws;
    }

    private static async Task<WebSocketReceiveResult> ReceiveAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await ws.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), cts.Token);
    }

    [Fact]
    public async Task A_plain_request_is_refused()
    {
        var response = await _host.GetTestClient().GetAsync(Path);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_handshake_without_a_token_is_closed()
    {
        using var ws = await ConnectAsync(token: null);

        var result = await ReceiveAsync(ws);

        result.MessageType.Should().Be(WebSocketMessageType.Close);
        ws.CloseStatusDescription.Should().Be("Missing credentials");
    }

    /// <summary>
    /// The bug: GitHub rejecting the token surfaced as <c>AuthorizationException</c>, and the
    /// handler answered it with <c>Response.StatusCode = 401</c> — after the 101 upgrade had
    /// already been sent. The setter threw, the exception escaped the endpoint, and the developer
    /// saw an aborted socket with no reason instead of being told their token was refused.
    /// </summary>
    [Fact]
    public async Task A_token_github_rejects_closes_the_socket_with_a_reason()
    {
        GitHubAnswers(401, "{\"message\":\"Bad credentials\"}");
        using var ws = await ConnectAsync("ghp_revoked");

        var result = await ReceiveAsync(ws);

        result.MessageType.Should().Be(WebSocketMessageType.Close);
        ws.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        ws.CloseStatusDescription.Should().Be("Unauthorized");
    }

    [Fact]
    public async Task A_developer_outside_the_allow_list_is_closed()
    {
        GitHubAnswers(200, "{\"login\":\"mallory\",\"id\":2}");
        using var ws = await ConnectAsync("ghp_mallory");

        var result = await ReceiveAsync(ws);

        result.MessageType.Should().Be(WebSocketMessageType.Close);
        ws.CloseStatusDescription.Should().Be("Unauthorized");
    }

    [Fact]
    public async Task An_allowed_developer_receives_forwarded_deliveries()
    {
        GitHubAnswers(200, "{\"login\":\"alice\",\"id\":1}");
        using var ws = await ConnectAsync("ghp_alice");
        var service = _host.Services.GetRequiredService<IDevWebSocketService>();
        var headers = new Dictionary<string, StringValues> { ["X-GitHub-Event"] = "push" };
        var context = new GitHubWebhookRoutingContext("push", "alice", "octo/repo", 1);

        // The registration happens after GitHub has answered, so keep offering the delivery until
        // the socket is registered and the first one arrives.
        var received = ws.ReadMessage();
        var clock = Stopwatch.StartNew();
        while (!received.IsCompleted && clock.Elapsed < Timeout)
        {
            await service.SendToClients(headers, "{\"n\":1}", context);
            await Task.WhenAny(received, Task.Delay(50));
        }

        (await received.WaitAsync(Timeout)).Should().Be("X-GitHub-Event: push\n\n{\"n\":1}");
        // Output only: the server notices CloseReceived and unregisters; it never acknowledges.
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
