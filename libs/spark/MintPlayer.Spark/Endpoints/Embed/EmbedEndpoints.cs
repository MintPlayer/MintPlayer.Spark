using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Embed;
using MintPlayer.Spark.Embed;

namespace MintPlayer.Spark.Endpoints.Embed;

/// <summary>
/// <c>/spark/embed</c> (commerce PRD X23): the embed's configuration, the top-level hand-over and the organiser's loader
/// script. Anonymous: an embed runs without the platform's cookies. Every endpoint answers 404 while embed mode is off.
/// </summary>
[MemberOf<SparkGroup>]
internal class EmbedGroup : IEndpointGroup
{
    public static string Prefix => "/embed";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.AllowAnonymous();
}

/// <summary><c>GET /spark/embed/config</c>: the allowed embedding origins (the embed's <c>postMessage</c> targets).</summary>
[MemberOf<EmbedGroup>]
internal sealed partial class GetEmbedConfig : IGetEndpoint
{
    public static string Path => "/config";

    [Inject] private readonly IOptions<SparkEmbedOptions> options;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var o = options.Value;
        if (!o.Enabled)
            return Task.FromResult(Results.NotFound());
        return Task.FromResult(Results.Json(new SparkEmbedConfig(
            o.AllowedOrigins.Where(SparkEmbed.IsValidOrigin).ToArray(), o.PathPrefixes.ToArray())));
    }
}

/// <summary>
/// <c>POST /spark/embed/handover</c>: a signed top-level URL carrying the embed's state. Called from inside a frame
/// whose XSRF cookie may be blocked, and it changes nothing server-side, so it carries no antiforgery check.
/// </summary>
[MemberOf<EmbedGroup>]
internal sealed partial class CreateEmbedHandover : IPostEndpoint<SparkEmbedHandoverRequest>
{
    public static string Path => "/handover";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly SparkEmbedHandoverService handover;
    [Inject] private readonly IOptions<SparkEmbedOptions> options;

    public override Task<IResult> HandleAsync(SparkEmbedHandoverRequest request, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return Task.FromResult(Results.NotFound());
        if (request is null)
            return Task.FromResult(Results.BadRequest());
        var result = handover.Create(request, out var error);
        return Task.FromResult(result is null
            ? Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest)
            : Results.Json(result));
    }
}

/// <summary><c>POST /spark/embed/handover/redeem</c>: the state of a hand-over token, on the top-level page. 404 for a bad or expired token.</summary>
[MemberOf<EmbedGroup>]
internal sealed partial class RedeemEmbedHandover : IPostEndpoint<SparkEmbedRedeemRequest>
{
    public static string Path => "/handover/redeem";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly SparkEmbedHandoverService handover;

    public override Task<IResult> HandleAsync(SparkEmbedRedeemRequest request, CancellationToken cancellationToken)
        => Task.FromResult(handover.Redeem(request?.Token) is { } state ? Results.Json(state) : Results.NotFound());
}

/// <summary><c>GET /spark/embed/loader.js</c>: the organiser's one script (<see cref="SparkEmbedLoaderScript"/>).</summary>
[MemberOf<EmbedGroup>]
internal sealed partial class GetEmbedLoader : IGetEndpoint
{
    public static string Path => "/loader.js";

    [Inject] private readonly IOptions<SparkEmbedOptions> options;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!options.Value.Enabled)
            return Task.FromResult(Results.NotFound());
        // Loaded by <script> from the organiser's origin: it must not be blocked by a same-origin resource policy.
        httpContext.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        httpContext.Response.Headers.CacheControl = "public, max-age=3600";
        return Task.FromResult(Results.Text(SparkEmbedLoaderScript.Source, "text/javascript; charset=utf-8"));
    }
}
