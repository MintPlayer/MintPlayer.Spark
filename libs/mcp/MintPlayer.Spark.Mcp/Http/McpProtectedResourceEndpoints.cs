using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Mcp.Authentication;

namespace MintPlayer.Spark.Mcp.Http;

/// <summary>
/// <c>GET /.well-known/oauth-protected-resource{mcp.Path}</c>: the RFC 9728 document (<see cref="McpProtectedResource"/>)
/// at the path-inserted location the challenge names (§3.1).
/// </summary>
/// <remarks>
/// Mapped only with a setup that offers OAuth (plan M3b, gap G2), and anonymous: an agent reads it before it has signed
/// in, so an application's fallback policy must not refuse it.
/// </remarks>
internal sealed partial class McpProtectedResourceDocument : IGetEndpoint
{
    /// <summary>The route with the default <c>mcp.Path</c>; <see cref="IEndpointBase.GetPath"/> maps the configured one.</summary>
    public static string Path => McpProtectedResource.WellKnown + "/mcp";

    /// <summary>The well-known prefix inserted before the host's <c>mcp.Path</c>. Read once, at map time, from the root provider.</summary>
    static string? IEndpointBase.GetPath(IServiceProvider services)
        => McpProtectedResource.WellKnown + services.GetRequiredService<SparkMcpOptions>().NormalizedPath;

    /// <summary>With personal tokens alone there is nowhere to sign in (PRD §4.2, setup 1). The setups are resolved by <c>UseSpark()</c>.</summary>
    static bool IEndpointBase.IsEnabled(IServiceProvider services) => services.GetRequiredService<McpAuthentication>().OffersOAuth;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.AllowAnonymous();

    [Inject] private readonly SparkMcpOptions options;
    [Inject] private readonly McpAuthentication authentication;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IHostEnvironment environment;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => Task.FromResult(McpProtectedResource.Document(httpContext, options, authentication, configuration, environment));
}

/// <summary>
/// <c>GET /.well-known/oauth-protected-resource</c>: the same document at the bare location, which some clients try
/// first.
/// </summary>
internal sealed partial class McpProtectedResourceRootDocument : IGetEndpoint
{
    public static string Path => McpProtectedResource.WellKnown;

    /// <summary>As at the path-inserted location: only with a setup that offers OAuth.</summary>
    static bool IEndpointBase.IsEnabled(IServiceProvider services) => services.GetRequiredService<McpAuthentication>().OffersOAuth;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.AllowAnonymous();

    [Inject] private readonly SparkMcpOptions options;
    [Inject] private readonly McpAuthentication authentication;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IHostEnvironment environment;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => Task.FromResult(McpProtectedResource.Document(httpContext, options, authentication, configuration, environment));
}
