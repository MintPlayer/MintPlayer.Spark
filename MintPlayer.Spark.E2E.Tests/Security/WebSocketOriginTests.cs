using System.Net;
using System.Net.WebSockets;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-H5 — WebSocket upgrades whose Origin header points at a foreign host must be
/// rejected. The default ASP.NET Core behavior is no origin check, which combined
/// with cookie auth on /spark/queries/{id}/stream lets an attacker page open a
/// WebSocket and ride the victim's session (CSWSH).
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class WebSocketOriginTests
{
    private static readonly Guid GetCompaniesQueryId = Guid.Parse("a20e8400-e29b-41d4-a716-446655440002");

    private readonly FleetE2ECollectionFixture _fixture;
    public WebSocketOriginTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WebSocket_upgrade_with_foreign_origin_is_rejected()
    {
        var ws = new ClientWebSocket();
        // Spark's middleware checks Request.Headers.Origin vs Request.Host. Setting
        // an Origin pointing at a different host MUST trip the 403 path.
        ws.Options.SetRequestHeader("Origin", "https://attacker.example");
        // The Fleet host uses a self-signed cert.
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var wssUrl = _fixture.Host.FleetUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            + $"/spark/queries/{GetCompaniesQueryId}/stream";

        var connectTask = ws.ConnectAsync(new Uri(wssUrl), CancellationToken.None);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connectTask);
        // The middleware sets 403 before AcceptWebSocketAsync, so the client sees
        // the handshake fail. .NET's ClientWebSocket surfaces this as
        // WebSocketException with HTTP-level detail.
        ex.Should().BeAssignableTo<WebSocketException>(
            "WebSocket upgrade with hostile Origin must fail at the handshake");
    }

    [Fact]
    public async Task WebSocket_upgrade_with_no_origin_is_accepted()
    {
        // Non-browser clients (curl, dotnet) don't send Origin — they're
        // not the threat model. The middleware lets them through.
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        var wssUrl = _fixture.Host.FleetUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            + $"/spark/queries/{GetCompaniesQueryId}/stream";

        try
        {
            // GetCompanies is granted to Everyone, so unauth + no Origin succeeds.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(wssUrl), cts.Token);
            ws.State.Should().BeOneOf(WebSocketState.Open, WebSocketState.CloseSent, WebSocketState.Closed,
                "no-Origin clients must be allowed through");
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", closeCts.Token);
            }
            ws.Dispose();
        }
    }
}
