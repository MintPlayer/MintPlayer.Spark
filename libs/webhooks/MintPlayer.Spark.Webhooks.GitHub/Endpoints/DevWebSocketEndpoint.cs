using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using System.Net.WebSockets;

namespace MintPlayer.Spark.Webhooks.GitHub.Endpoints;

/// <summary>
/// <c>GET {DevWebSocketPath}</c> (default <see cref="DefaultPath"/>): the production end of the dev
/// tunnel. A developer's machine opens a WebSocket here, proves who it is with a GitHub token, and from
/// then on receives the webhook deliveries addressed to <see cref="GitHubWebhooksOptions.DevelopmentAppId"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exists only when <see cref="GitHubWebhooksOptions.DevelopmentAppId"/> is set</b>
/// (<see cref="IEndpointBase.IsEnabled"/>), read from <c>IOptions</c> so that a value set through
/// <c>Configure&lt;GitHubWebhooksOptions&gt;()</c> counts too. <see cref="IDevWebSocketService"/> is
/// registered unconditionally, so a mapped endpoint can always be activated, while an unmapped one is
/// never activated and its <c>[Inject]</c> dependency never built.
/// </para>
/// <para>
/// ⚠️ A GET, not a bare <c>Map</c>. A bare <c>Map()</c> constrains no HTTP method, so this endpoint used
/// to match POST/PUT/PATCH/DELETE as well — a mutating verb under the <c>/spark</c> prefix carrying no
/// antiforgery metadata. Nothing was exploitable through it (the handler 400s anything that is not a
/// WebSocket upgrade) but it was the one hole in the <c>/spark</c> surface no gate could close, and a
/// WebSocket handshake is a GET by definition.
/// </para>
/// <para>
/// Cross-site WebSocket hijacking is refused before this endpoint runs, by Spark's same-origin guard on
/// every upgrade (<c>SparkMiddleware</c>, after <c>UseWebSockets()</c>).
/// </para>
/// </remarks>
internal sealed partial class DevWebSocketEndpoint : IGetEndpoint
{
    /// <summary>The route when <see cref="GitHubWebhooksOptions.DevWebSocketPath"/> is not changed; that option defaults to this.</summary>
    public const string DefaultPath = "/spark/github/dev-ws";

    public static string Path => DefaultPath;

    /// <summary>The host's <see cref="GitHubWebhooksOptions.DevWebSocketPath"/>. Read once, at map time, from the root provider.</summary>
    static string? IEndpointBase.GetPath(IServiceProvider services)
        => services.GetRequiredService<IOptions<GitHubWebhooksOptions>>().Value.DevWebSocketPath;

    /// <summary>Mapped only when a development app is configured; see the remarks on the class.</summary>
    static bool IEndpointBase.IsEnabled(IServiceProvider services)
        => services.GetRequiredService<IOptions<GitHubWebhooksOptions>>().Value.DevelopmentAppId.HasValue;

    // Through the factory rather than `new GitHubClient`, so the call can be pointed at a stand-in
    // server; it was the one GitHub call in the package that could not.
    [Inject] private readonly IGitHubClientFactory gitHubClientFactory;
    [Inject] private readonly IOptions<GitHubWebhooksOptions> options;
    [Inject] private readonly IDevWebSocketService devWebSocketService;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
            return Results.BadRequest();

        var ws = await httpContext.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = "wss",
            KeepAliveInterval = TimeSpan.FromMinutes(5),
        });

        // From here on the 101 upgrade has been sent: every outcome is a close with a reason, never a
        // status code, and the handler answers Results.Empty so nothing tries to write a response.
        try
        {
            // Receive handshake with GitHub token
            var handshake = await ws.ReadObject<Handshake>();
            if (handshake?.GithubToken == null)
            {
                await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "Missing credentials", CancellationToken.None);
                return Results.Empty;
            }

            // Validate GitHub token and resolve username
            var githubUser = await gitHubClientFactory.CreateUserClient(handshake.GithubToken).User.Current();

            // R2-H12: empty AllowedDevUsers fails closed — every webhook delivered to this app's
            // DevelopmentAppId carries private-repo data, so accepting "any authenticated GitHub user"
            // by default (the prior behavior) leaked all of it to throwaway accounts. Operators MUST
            // configure AllowedDevUsers explicitly to enable the dev tunnel.
            var allowed = options.Value.AllowedDevUsers;
            if (allowed.Count == 0 || !allowed.Contains(githubUser.Login))
            {
                await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "Unauthorized", CancellationToken.None);
                return Results.Empty;
            }

            await devWebSocketService.NewSocketClient(new SocketClient(ws, githubUser.Login));
        }
        catch (Octokit.AuthorizationException)
        {
            // GitHub refused the token. Closed with a reason, not answered with a 401: the 101 upgrade
            // has already been sent, so setting StatusCode here threw, the exception escaped the
            // endpoint, and the developer saw an aborted socket with no explanation.
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None);
        }

        return Results.Empty;
    }
}
