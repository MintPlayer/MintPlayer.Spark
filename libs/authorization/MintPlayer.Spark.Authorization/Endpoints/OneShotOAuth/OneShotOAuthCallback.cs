using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.OneShotOAuth;

namespace MintPlayer.Spark.Authorization.Endpoints.OneShotOAuth;

/// <summary>
/// <c>GET /spark/auth/oauth-callback</c>: where a one-shot OAuth client's provider redirects (#554 f1).
/// Serves the popup page that tells the opener the step is done; the token stays on the server.
/// </summary>
/// <remarks>
/// Mapped only when at least one client is registered (<c>spark.AddOneShotOAuthClient</c>). Requires a
/// signed-in user, and never signs one in: the attempt it completes is bound to the user who started it.
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class OneShotOAuthCallback : IGetEndpoint
{
    public static string Path => "/oauth-callback";

    static bool IEndpointBase.IsEnabled(IServiceProvider services)
        => services.GetService<IOptions<SparkOneShotOAuthOptions>>()?.Value.Clients.Count > 0;

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
    {
        builder.RequireAuthorization();
    }

    [QueryParam] public string? Code { get; set; }
    [QueryParam] public string? State { get; set; }
    [QueryParam] public string? Error { get; set; }

    [Inject] private readonly SparkOneShotOAuthCallback callback;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var result = await callback.CompleteAsync(State, Code, Error, httpContext.RequestAborted);
        return SparkAuthenticationExtensions.PopupPage(result.Nonce, result.Error, SparkAuthenticationExtensions.PopupTexts.Authorize);
    }
}
