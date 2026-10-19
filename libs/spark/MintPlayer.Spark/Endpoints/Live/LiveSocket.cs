using System.Net.WebSockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Live;

namespace MintPlayer.Spark.Endpoints.Live;

/// <summary>
/// <c>GET|CONNECT /spark/live</c>: the one multiplexed socket for live updates (X4,
/// <c>docs/guide-live-updates.md</c>). Persistent objects and query pages are subscribed to over it, and
/// every push re-runs the HTTP read path under the caller's rights.
/// </summary>
/// <remarks>
/// It replaces <c>/spark/queries/{id}/stream</c>, and with it the last route variable in Spark: what used to be
/// in the path is now in the <c>subscribe</c> frame. Mutations never travel over it — subscriptions are reads —
/// so antiforgery stays HTTP-only, and the same-origin guard in <c>UseSpark()</c> (which keys off
/// <c>IsWebSocketRequest</c>) is the CSWSH protection, as it was for the streaming endpoint.
/// </remarks>
[MemberOf<SparkGroup>]
internal sealed partial class LiveSocket : IEndpoint
{
    public static string Path => "/live";

    // CONNECT is not optional here, and GET alone is not enough for a browser.
    //
    // Kestrel advertises SETTINGS_ENABLE_CONNECT_PROTOCOL (verified: true), so on an HTTP/2 origin a browser
    // opens a WebSocket the RFC 8441 way — `:method = CONNECT` with `:protocol = websocket` — and never
    // attempts the HTTP/1.1 `GET` + `Upgrade` handshake. Endpoint routing then rejects the method before the
    // handler runs, and the client sees `405 Method Not Allowed` with no server-side log to explain it.
    //
    // Accepting CONNECT is not a widening. A CONNECT that is not a valid WebSocket upgrade leaves
    // `WebSockets.IsWebSocketRequest` false and falls out at the 400 below, and the same-origin guard keys off
    // that same flag, so CSWSH protection covers both protocols identically.
    public static IEnumerable<string> Methods => ["GET", "CONNECT"];

    [Inject] private readonly LiveHub hub;
    [Inject] private readonly IOptions<SparkLiveOptions> options;
    // The request scope (what HttpContext.RequestServices is), injected rather than located (EndpointConventionsTests R1).
    [Inject] private readonly IServiceProvider services;
    // Optional: an app without authentication (DemoApp) registers no scheme provider at all.
    [Inject] private readonly IAuthenticationSchemeProvider? schemes;


    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
            return Results.Json(new { error = "WebSocket connection required" }, statusCode: 400);

        // Before the upgrade: once the response has started, an authentication handler can no longer run.
        // The lifetime is drawn between 90 % and 100 % of the maximum (commerce PRD X2): sockets opened together
        // at a sale's start would otherwise all reconnect in the same second when it runs out.
        var deadline = DateTimeOffset.UtcNow + options.Value.MaxSocketLifetime * (0.9 + 0.1 * Random.Shared.NextDouble());
        if (await TicketExpiryAsync(httpContext) is { } expires && expires < deadline)
            deadline = expires;

        // Who the socket belongs to, frozen now (commerce PRD X2): the signed-in user, else the visitor cookie,
        // else the address (an IPv6 one grouped to its /64). A sign-in reconnects, so it is never older than the
        // socket's lifetime.
        var visitor = Visitor.SparkVisitorFeature.For(httpContext);
        var viewer = new SparkLiveViewer(visitor.IdentityKey, visitor.UserId, visitor.VisitorId);

        using var socket = await httpContext.WebSockets.AcceptWebSocketAsync();
        var userKey = viewer.IdentityKey;
        if (!hub.TryAcquireSocket(userKey))
        {
            await socket.CloseAsync((WebSocketCloseStatus)LiveCodes.CloseTooManySockets, "Too many live connections", CancellationToken.None);
            return Results.Empty;
        }

        try
        {
            await new LiveConnection(socket, services, deadline, viewer).RunAsync(httpContext.RequestAborted);
        }
        finally
        {
            hub.ReleaseSocket(userKey);
        }
        return Results.Empty;
    }

    /// <summary>When the caller's login ticket expires, if they have one with an expiry.</summary>
    private async Task<DateTimeOffset?> TicketExpiryAsync(HttpContext httpContext)
    {
        // Optional: an app without authentication (DemoApp) registers no scheme provider at all.
        if (httpContext.User.Identity?.IsAuthenticated != true
            || schemes is null
            || await schemes.GetDefaultAuthenticateSchemeAsync() is null)
            return null;
        var result = await httpContext.AuthenticateAsync();
        return result.Succeeded ? result.Properties?.ExpiresUtc : null;
    }
}
