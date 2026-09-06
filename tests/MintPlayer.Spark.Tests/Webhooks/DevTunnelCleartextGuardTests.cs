using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;
using Xunit;

namespace MintPlayer.Spark.Tests.Webhooks;

/// <summary>
/// The dev tunnel's credential-leak guard.
/// <para>
/// The WebSocket handshake's first frame carries a GitHub personal access token. An operator who
/// points <c>ProductionWebSocketUrl</c> at a misconfigured <c>ws://example.test</c> would send a
/// live credential in cleartext to whatever is listening — and see no error, because the tunnel
/// connects perfectly happily. This guard is the only thing standing between that mistake and a
/// leaked token, and it had <b>no test</b>: it lived inline in a <c>BackgroundService</c> loop that
/// swallows exceptions and retries every five seconds, so deleting or inverting it would have
/// produced nothing louder than a log line.
/// </para>
/// </summary>
public class DevTunnelCleartextGuardTests
{
    /// <summary>
    /// Plain ws to anywhere off-machine is refused. The message has to name the URL, because the
    /// whole point is that the operator mistyped it.
    /// </summary>
    [Theory]
    [InlineData("ws://example.test/hook")]
    [InlineData("ws://192.0.2.10:8080/hook")]
    [InlineData("WS://EXAMPLE.TEST/hook")]   // scheme comparison must be case-insensitive
    public void Plain_ws_to_a_non_loopback_host_is_refused(string url)
    {
        var uri = new Uri(url);

        var ex = Assert.Throws<InvalidOperationException>(
            () => WebSocketDevClientService.EnsureTokenWillNotTravelInCleartext(uri));

        Assert.Contains("cleartext", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(uri.Host, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loopback over plain ws is allowed on purpose: it is the ordinary local development case, the
    /// traffic never leaves the machine, and demanding a certificate for localhost would push people
    /// towards turning the check off entirely — which is strictly worse than allowing this.
    /// </summary>
    [Theory]
    [InlineData("ws://localhost:5000/hook")]
    [InlineData("ws://127.0.0.1:5000/hook")]
    [InlineData("ws://[::1]:5000/hook")]
    public void Plain_ws_to_loopback_is_allowed(string url)
        => WebSocketDevClientService.EnsureTokenWillNotTravelInCleartext(new Uri(url));

    /// <summary>Encrypted transport is always fine, loopback or not.</summary>
    [Theory]
    [InlineData("wss://example.test/hook")]
    [InlineData("wss://localhost:5000/hook")]
    [InlineData("WSS://EXAMPLE.TEST/hook")]
    public void Encrypted_transport_is_allowed_anywhere(string url)
        => WebSocketDevClientService.EnsureTokenWillNotTravelInCleartext(new Uri(url));
}
