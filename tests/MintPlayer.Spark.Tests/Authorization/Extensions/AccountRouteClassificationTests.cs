using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// #460 D16: every account route is classified per <see cref="SparkLocalCredentials"/> mode and every
/// mutating one carries the antiforgery stamp. The table below is the specification, written out rather
/// than derived from the code, and <b>every</b> <c>/spark/auth/manage/*</c> route in the route table
/// must appear in it — a new manage route nobody classified fails here.
/// </summary>
public class AccountRouteClassificationTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string Full = "F", SignInOnly = "S", Disabled = "D";

    /// <summary>"METHOD route" → the modes that map it.</summary>
    private static readonly Dictionary<string, string> Expected = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST /spark/auth/register"] = Full,
        ["POST /spark/auth/resendConfirmationEmail"] = Full,
        ["POST /spark/auth/login"] = Full + SignInOnly,
        ["POST /spark/auth/refresh"] = Full + SignInOnly,
        ["POST /spark/auth/forgotPassword"] = Full + SignInOnly,
        ["POST /spark/auth/resetPassword"] = Full + SignInOnly,
        ["GET /spark/auth/confirmEmail"] = Full + SignInOnly + Disabled,
        ["POST /spark/auth/confirm-email"] = Full + SignInOnly + Disabled,
        ["GET /spark/auth/manage/info"] = Full + SignInOnly + Disabled,
        ["POST /spark/auth/manage/info"] = Full + SignInOnly,
        ["POST /spark/auth/manage/password"] = Full + SignInOnly,
        ["POST /spark/auth/manage/2fa"] = Full + SignInOnly + Disabled,
        ["GET /spark/auth/manage/2fa/authenticator-uri"] = Full + SignInOnly + Disabled,
        ["GET /spark/auth/manage/profile"] = Full + SignInOnly + Disabled,
        ["POST /spark/auth/manage/profile"] = Full + SignInOnly + Disabled,
        ["GET /spark/auth/manage/personal-data"] = Full + SignInOnly + Disabled,
        ["DELETE /spark/auth/manage/account"] = Full + SignInOnly + Disabled,
    };

    private static string Code(SparkLocalCredentials mode) => mode switch
    {
        SparkLocalCredentials.Full => Full,
        SparkLocalCredentials.SignInOnly => SignInOnly,
        _ => Disabled,
    };

    [Theory]
    [InlineData(SparkLocalCredentials.Full)]
    [InlineData(SparkLocalCredentials.SignInOnly)]
    [InlineData(SparkLocalCredentials.Disabled)]
    public async Task Each_mode_maps_exactly_its_account_routes_and_stamps_every_mutating_one(SparkLocalCredentials mode)
    {
        await using var host = await AccountTestHost.StartAsync(Store, mode);

        var endpoints = host.Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (Key: $"{method} {e.RoutePattern.RawText}", Endpoint: e)))
            .ToArray();
        var mapped = endpoints.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (route, modes) in Expected)
        {
            if (modes.Contains(Code(mode)))
                mapped.Should().Contain(route, $"{mode} maps {route}");
            else
                mapped.Should().NotContain(route, $"{mode} must not map {route}");
        }

        // Nothing under /manage escapes the table.
        endpoints.Where(e => e.Endpoint.RoutePattern.RawText!.StartsWith("/spark/auth/manage/", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Key)
            .Should().OnlyContain(key => Expected.ContainsKey(key));

        foreach (var (key, endpoint) in endpoints.Where(e => Expected.ContainsKey(e.Key)))
        {
            var mutating = !key.StartsWith("GET ", StringComparison.OrdinalIgnoreCase);
            var stamp = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>();
            if (mutating && !StatedExemptions.ContainsKey(key))
                (stamp?.RequiresValidation ?? false).Should().BeTrue($"{key} mutates and must carry the antiforgery stamp");
        }
    }

    /// <summary>
    /// Mutating routes deliberately without the stamp, each with its reason — an exemption is stated,
    /// never implied by absence.
    /// </summary>
    private static readonly Dictionary<string, string> StatedExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST /spark/auth/refresh"] = "Microsoft's bearer refresh: the refresh token travels in the body, so there is no ambient credential for a cross-site request to ride on.",
    };
}
