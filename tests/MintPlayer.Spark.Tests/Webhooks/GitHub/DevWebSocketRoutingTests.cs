using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// Routing of forwarded webhooks to connected developers.
/// <para>
/// Before this existed, <c>SendToClients</c> fanned every delivery out to every connected
/// developer — so two developers on the dev tunnel each saw the other's webhook traffic.
/// <c>AllowedDevUsers</c> did not help: it gates who may connect, not who receives what.
/// </para>
/// </summary>
public class DevWebSocketRoutingTests
{
    /// <summary>A WebSocket that stays Open and records what was written to it.</summary>
    private sealed class RecordingWebSocket : WebSocket
    {
        public List<string> Sent { get; } = [];

        public override WebSocketState State => WebSocketState.Open;

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static readonly Dictionary<string, StringValues> Headers =
        new() { ["X-GitHub-Event"] = "pull_request" };

    private static GitHubWebhookRoutingContext ContextFrom(string senderLogin)
        => new("pull_request", senderLogin, "MintPlayer/Spark", 42);

    private static (DevWebSocketService Service, RecordingWebSocket Alice, RecordingWebSocket Bob) Build(
        Func<GitHubWebhookRoutingContext, string, bool>? filter = null)
    {
        var options = new GitHubWebhooksOptions();
        if (filter is not null)
            options.DevSocketFilter = filter;

        var service = new DevWebSocketService(Options.Create(options));

        var alice = new RecordingWebSocket();
        var bob = new RecordingWebSocket();

        // NewSocketClient blocks for the socket's lifetime; fire and forget to register them.
        _ = service.NewSocketClient(new SocketClient(alice, "alice"));
        _ = service.NewSocketClient(new SocketClient(bob, "bob"));

        return (service, alice, bob);
    }

    [Fact]
    public async Task Sends_only_to_the_developer_who_caused_the_delivery()
    {
        var (service, alice, bob) = Build();

        await service.SendToClients(Headers, """{"action":"opened"}""", ContextFrom("alice"));

        alice.Sent.Count.Should().Be(1);
        bob.Sent.Count.Should().Be(0);
    }

    [Fact]
    public async Task Matches_the_sender_login_case_insensitively()
    {
        var (service, alice, _) = Build();

        await service.SendToClients(Headers, "{}", ContextFrom("ALICE"));

        alice.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task Falls_back_to_everyone_when_the_payload_has_no_sender()
    {
        // An unattributed delivery must not vanish.
        var (service, alice, bob) = Build();

        await service.SendToClients(Headers, "{}", ContextFrom(string.Empty));

        alice.Sent.Count.Should().Be(1);
        bob.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task Sends_to_nobody_when_the_sender_matches_no_connected_developer()
    {
        var (service, alice, bob) = Build();

        await service.SendToClients(Headers, "{}", ContextFrom("carol"));

        alice.Sent.Count.Should().Be(0);
        bob.Sent.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_custom_filter_can_restore_the_old_broadcast_behaviour()
    {
        var (service, alice, bob) = Build(static (_, _) => true);

        await service.SendToClients(Headers, "{}", ContextFrom("carol"));

        alice.Sent.Count.Should().Be(1);
        bob.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_custom_filter_receives_the_full_routing_context()
    {
        var (service, alice, _) = Build((context, _) => context.RepositoryFullName == "MintPlayer/Spark");

        await service.SendToClients(Headers, "{}", ContextFrom("carol"));

        alice.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task Forwards_the_headers_and_body_in_the_wire_format_the_dev_client_parses()
    {
        var (service, alice, _) = Build();

        await service.SendToClients(Headers, """{"action":"opened"}""", ContextFrom("alice"));

        alice.Sent[0].Should().Be("X-GitHub-Event: pull_request\n\n{\"action\":\"opened\"}");
    }
}
