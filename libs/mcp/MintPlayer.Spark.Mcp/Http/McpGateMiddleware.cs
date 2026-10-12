using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Mcp.Authentication;

namespace MintPlayer.Spark.Mcp.Http;

/// <summary>Marks the endpoints <c>AddMcp</c> maps, so the gate knows them by metadata rather than by path.</summary>
internal sealed class SparkMcpEndpointMarker;

/// <summary>
/// Everything about a <c>/mcp</c> request that is decided before the SDK reads the JSON-RPC body (MCP PRD
/// D3, D16, D17): the <c>Origin</c> check, who the caller is (bearer tokens only — cookies are ignored), the
/// <c>RequireSignIn</c> 401, the call limits, and the application's permission for this agent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a middleware and not only the endpoint policy.</b> The bearer-only policy <c>AddMcp</c> puts on the
/// endpoint makes the authorization middleware re-authenticate with the bearer schemes and replace
/// <c>HttpContext.User</c> — but only when there is at least one scheme: a policy naming none keeps the
/// composite's principal, which is the cookie's (spike S1b). So the guarantee is here, for every case: the
/// principal is what a bearer scheme produced, or an empty one. The SDK maps its handler as a plain
/// <c>RequestDelegate</c>, which endpoint filters do not wrap, hence a middleware keyed on
/// <see cref="SparkMcpEndpointMarker"/>.
/// </para>
/// <para>
/// <b>The scheme feature (spike S1 finding 1).</b> The composite records the scheme that authenticated the
/// request in <see cref="ISparkAuthenticatedSchemeFeature"/>; after the principal is swapped it still names
/// the cookie. It is reset here to the bearer scheme, or removed for an anonymous call, so nothing downstream
/// (the antiforgery gate, <c>ISparkCurrentUser</c>) decides from a credential this endpoint ignored.
/// </para>
/// </remarks>
internal sealed class McpGateMiddleware(
    RequestDelegate next,
    SparkMcpOptions options,
    McpAuthentication authentication,
    McpRateLimiter limiter,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<McpGateMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<SparkMcpEndpointMarker>() is null)
        {
            await next(context);
            return;
        }

        // MCP spec (basic/transports): servers MUST validate Origin, against DNS rebinding. Ours, not the SDK's,
        // which spike S1 never probed.
        if (!McpOrigin.IsAllowed(context, options, configuration, environment))
        {
            logger.LogWarning("MCP request refused: Origin {Origin} is not this server's.", context.Request.Headers.Origin.ToString());
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Forbidden: this Origin may not call the MCP endpoint.");
            return;
        }

        var setup = await AuthenticateAsync(context);

        // The agent and the user's grant, read now (D12): a grant revoked since the token was issued refuses the
        // call like a token nobody accepts.
        var credential = setup is null ? null : await setup.Source.DescribeAsync(context.User, context.RequestAborted);
        if (setup is not null && credential is null)
        {
            setup = null;
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
            context.Features.Set<ISparkAuthenticatedSchemeFeature>(null);
        }

        if (setup is null && context.Request.Headers.Authorization.Count > 0)
        {
            // A credential was presented and nothing accepted it: say so, never fall back to anonymous (F7). This
            // also refuses every credential that is not an agent token (covt_, GitHub OIDC; MCP PRD D23).
            await ChallengeAsync(context, "The token was not accepted. " + McpSignInText.For(authentication, options.ServerName), invalidToken: true);
            return;
        }

        if (setup is null && options.RequireSignIn)
        {
            // MCP PRD D17 (b): no client signs in on a mid-session 401, so an app with nothing for anonymous agents
            // makes every unauthenticated request, initialize included, ask for sign-in at connect.
            await ChallengeAsync(context, McpSignInText.For(authentication, options.ServerName), invalidToken: false);
            return;
        }

        if (!limiter.TryAcquire(context, setup is not null))
        {
            context.Response.Headers.RetryAfter = "60";
            await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "Too many requests: the call limit for this user or address was reached. Wait a minute.");
            return;
        }

        var agent = credential?.Agent ?? AgentInformation.Anonymous(context.User);
        var app = McpAgentExposure.AppPermissions(options, agent);

        context.Features.Set(new McpRequestState { Agent = agent, Setup = setup, Grant = credential?.Grant, AppPermissions = app });

        // Proxies must not buffer the SSE response (guide-mcp.md "Deployment").
        context.Response.Headers["X-Accel-Buffering"] = "no";

        if (setup is not null || !authentication.OffersOAuth)
        {
            await next(context);
            return;
        }

        // D17 (M3b): an anonymous call on a server that offers OAuth. If a tool refuses it (SparkMcpResult.Refuse), the
        // answer must be a real HTTP 401 with the challenge, since an HTTP 401 is what makes an agent offer its own
        // sign-in (spike S11) — and a status cannot change once the SDK has started streaming. So the response is held
        // back until the SDK is done. Stateless mode answers each POST in one piece anyway (D2).
        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new MemoryStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer));
        try
        {
            await next(context);
        }
        finally
        {
            context.Features.Set(original);
        }

        var state = McpRequestState.Of(context);
        if (state is { SignInRequired: true } && !context.Response.HasStarted)
        {
            context.Response.Clear();
            await ChallengeAsync(context, McpSignInText.For(authentication, options.ServerName), invalidToken: false, state.JsonRpcId);
            return;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original.Stream, context.RequestAborted);
    }

    /// <summary>
    /// Runs the wired bearer schemes only, adopts the first that succeeds, and makes the request anonymous
    /// otherwise. The composite (and with it the cookie) never decides who calls <c>/mcp</c>.
    /// </summary>
    private async Task<McpAuthenticationSetup?> AuthenticateAsync(HttpContext context)
    {
        foreach (var setup in authentication.Setups)
        {
            var result = await context.AuthenticateAsync(setup.Scheme);
            if (result.Succeeded)
            {
                context.User = result.Principal;
                context.Features.Set<ISparkAuthenticatedSchemeFeature>(
                    new McpAuthenticatedScheme(new SparkCredentialScheme(setup.Scheme, IsAmbient: false, setup.SubjectIsLocalUserId)));
                return setup;
            }
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        context.Features.Set<ISparkAuthenticatedSchemeFeature>(null);
        return null;
    }

    /// <summary>
    /// A 401 with the challenge (RFC 9728 when OAuth is offered) and a JSON-RPC error body whose message tells the
    /// agent how to start its client's own sign-in (D17). The body names the call's id: the refused tool call's, or
    /// the request body's when the gate refuses before the SDK has read it.
    /// </summary>
    private async Task ChallengeAsync(HttpContext context, string message, bool invalidToken, JsonNode? id = null)
    {
        id ??= await ReadJsonRpcIdAsync(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = McpProtectedResource.Challenge(context, options, authentication, invalidToken);
        await context.Response.WriteAsJsonAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = UnauthorizedErrorCode, ["message"] = message },
        });
    }

    /// <summary>The JSON-RPC error code of a sign-in refusal: implementation-defined server error range (-32000 to -32099).</summary>
    internal const int UnauthorizedErrorCode = -32001;

    /// <summary>The <c>id</c> of a JSON-RPC request body, read without consuming it; <see langword="null"/> for anything else.</summary>
    private static async Task<JsonNode?> ReadJsonRpcIdAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.ContentLength is > MaxPeekBytes)
            return null;
        try
        {
            context.Request.EnableBuffering(MaxPeekBytes);
            var node = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            context.Request.Body.Position = 0;
            return node is JsonObject message && message.TryGetPropertyValue("id", out var id) ? id : null;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    private const int MaxPeekBytes = 64 * 1024;

    private static Task WriteErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }

    private sealed class McpAuthenticatedScheme(SparkCredentialScheme scheme) : ISparkAuthenticatedSchemeFeature
    {
        public SparkCredentialScheme Scheme { get; } = scheme;
    }
}
