using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-H5 — WebSocket upgrades whose Origin header points at a foreign host must be
/// rejected. The default ASP.NET Core behavior is no origin check, which combined
/// with cookie auth on the live socket (<c>/spark/live</c>) lets an attacker page open a
/// WebSocket and ride the victim's session (CSWSH).
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class WebSocketOriginTests
{
    // StreamCompanies — a live app-stream query on Company, granted to the anonymous group (QueryRead/Company).
    private static readonly Guid StreamCompaniesQueryId = Guid.Parse("a20e8400-e29b-41d4-a716-446655440004");

    private readonly FleetE2ECollectionFixture _fixture;
    public WebSocketOriginTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    private string LiveUrl => _fixture.Host.FleetUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase) + "/spark/live";

    [Fact]
    public async Task WebSocket_upgrade_with_foreign_origin_is_rejected()
    {
        var ws = new ClientWebSocket();
        // Spark's middleware checks Request.Headers.Origin vs Request.Host. Setting
        // an Origin pointing at a different host MUST trip the 403 path.
        ws.Options.SetRequestHeader("Origin", "https://attacker.example");
        // The Fleet host uses a self-signed cert.
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var connectTask = ws.ConnectAsync(new Uri(LiveUrl), CancellationToken.None);

        var ex = (await new Func<Task>(() => connectTask).Should().ThrowAsync<Exception>()).Which;
        // The middleware sets 403 before AcceptWebSocketAsync, so the client sees
        // the handshake fail. .NET's ClientWebSocket surfaces this as
        // WebSocketException with HTTP-level detail.
        ex.Should().BeAssignableTo<WebSocketException>(
            "WebSocket upgrade with hostile Origin must fail at the handshake");
    }

    [Fact]
    public async Task WebSocket_upgrade_with_no_origin_is_accepted_and_serves_the_app_stream()
    {
        // Non-browser clients (curl, dotnet) don't send Origin — they're
        // not the threat model. The middleware lets them through.
        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await ws.ConnectAsync(new Uri(LiveUrl), cts.Token);
        ws.State.Should().Be(WebSocketState.Open, "no-Origin clients must be allowed through");

        var subscribe = JsonSerializer.Serialize(new { op = "subscribe", sub = "c", query = new { queryId = StreamCompaniesQueryId.ToString(), take = 10 } });
        await ws.SendAsync(Encoding.UTF8.GetBytes(subscribe), WebSocketMessageType.Text, true, cts.Token);

        // The app stream yields three companies at once; the first frame is their page, through the
        // same row security and projection as /execute.
        var frame = await ReceiveAsync(ws, cts.Token);
        frame.GetProperty("type").GetString().Should().Be("result");
        frame.GetProperty("sub").GetString().Should().Be("c");
        frame.GetProperty("result").GetProperty("totalItems").GetInt32().Should().Be(3);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var received = await ws.ReceiveAsync(buffer, cancellationToken);
            message.Write(buffer, 0, received.Count);
            if (!received.EndOfMessage)
                continue;
            var frame = JsonDocument.Parse(message.ToArray()).RootElement.Clone();
            if (frame.GetProperty("type").GetString() != "ping")
                return frame;
            message.SetLength(0);
        }
    }
}
