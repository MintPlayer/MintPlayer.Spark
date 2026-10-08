using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace HR.Api;

/// <summary>
/// <c>GET /api/hr/fleet-cars</c>: the HR half of the resource-server demo (<c>docs/identity_provider_platform_PRD.md</c>
/// I12). Fleet's cars, read with the signed-in user's SparkId access token, which carries <c>fleet.read</c> when the
/// user granted it.
/// </summary>
/// <remarks>
/// 409 when there is no usable token: the user signed in another way, did not grant <c>fleet.read</c>, or the token
/// expired. Signing in through SparkId again fetches a fresh one.
/// </remarks>
internal sealed partial class FleetCarsProxyEndpoint : IGetEndpoint
{
    public static string Path => "/api/hr/fleet-cars";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization();

    [Inject] private readonly UserManager<SparkUser> users;
    [Inject] private readonly IHttpClientFactory clients;
    [Inject] private readonly IConfiguration configuration;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (await users.GetUserAsync(httpContext.User) is not { } user
            || await users.GetAuthenticationTokenAsync(user, "SparkId", "access_token") is not { Length: > 0 } accessToken)
            return Results.Problem("Sign in through Spark Identity first.", statusCode: StatusCodes.Status409Conflict);

        var fleet = configuration["Demo:FleetBaseUrl"] ?? "https://localhost:5003";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{fleet.TrimEnd('/')}/api/fleet/cars");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await clients.CreateClient().SendAsync(request, httpContext.RequestAborted);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return Results.Problem("Fleet refused the token: it lacks fleet.read or has expired. Sign in through Spark Identity again.",
                statusCode: StatusCodes.Status409Conflict);
        response.EnsureSuccessStatusCode();
        return Results.Content(await response.Content.ReadAsStringAsync(httpContext.RequestAborted), "application/json");
    }
}
