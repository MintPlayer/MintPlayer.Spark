using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Enabling the identity provider must not put a CORS policy on the rest of Spark.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>It did.</b> <c>EnableDynamicCors</c> defaults to <see langword="true"/> and the policy is
/// <c>SetIsOriginAllowed(_ =&gt; true)</c>; it was applied by a bare <c>app.UseCors("SparkOidcCors")</c>,
/// which is pipeline-wide. So an application that turned the identity provider on also let any page on
/// any origin read the anonymous view of <c>/spark/types</c>, <c>/spark/translations</c>,
/// <c>/spark/permissions/*</c> and <c>/spark/auth/capabilities</c> — with no
/// <c>Access-Control-Allow-Credentials</c>, so never the caller's own data, but a surface nobody asked
/// for and nothing documented.
/// </para>
/// <para>
/// The policy's inline comment said <c>// Validated at runtime below</c>. There was no such validation:
/// <c>OidcApplication.AllowedCorsOrigins</c> is declared, documented, and read by nothing. That is why
/// this is a test and not a code comment — the code comment was the problem.
/// </para>
/// <para>
/// What this pins is the <b>scope</b>, which is the part that was wrong. Any-origin on the protocol
/// endpoints themselves is a separate decision, and a defensible one: a public OIDC client is a browser
/// app with no secret, so <c>/token</c> is meant to be called cross-origin and its control is PKCE, not
/// the <c>Origin</c> header. Narrowing that to each application's registered origins is tracked in
/// <c>docs/leftovers.md</c>.
/// </para>
/// </remarks>
public class OidcCorsScopeTests : SparkTestDriver
{
    private const string HostileOrigin = "https://evil.test";

    private SparkEndpointFactory<OidcTestContext> CreateFactory(bool enableCors) =>
        new(
            Store,
            models: [],
            configureSpark: spark =>
            {
                // Full, explicitly: the default refuses to boot without an external provider, and this
                // fixture is about CORS scope rather than credential modes.
                spark.AddAuthentication<SparkUser>(
                    configure: auth => auth.LocalCredentials = MintPlayer.Spark.Authorization.Configuration.SparkLocalCredentials.Full);
                spark.AddIdentityProvider(options =>
                {
                    options.Issuer = "https://idp.test";
                    options.EnableDynamicCors = enableCors;
                    options.SigningKeyPath = Path.Combine(
                        Path.GetTempPath(), "spark-oidc-cors-" + Guid.NewGuid().ToString("N") + ".json");
                });
            },
            environment: "Development");

    /// <summary>A CORS preflight, which is what actually reveals whether a policy applies.</summary>
    private static HttpRequestMessage Preflight(string path, string method = "GET")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", HostileOrigin);
        request.Headers.Add("Access-Control-Request-Method", method);
        return request;
    }

    /// <summary>
    /// The default: enabling the identity provider grants no cross-origin access anywhere.
    /// </summary>
    /// <remarks>
    /// ⚠️ The protocol endpoints are in this list deliberately. A cross-origin SPA client is a real
    /// scenario, but it is not the built-in one — Spark serves an application's own Angular frontend
    /// from the same host, so it never needed CORS. Granting it by default was paying for a scenario
    /// nobody in this repository has, with a permission the caller did not ask for.
    /// </remarks>
    [Theory]
    [InlineData("/spark/types")]
    [InlineData("/spark/translations")]
    [InlineData("/spark/auth/capabilities")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/connect/token")]
    public async Task By_default_the_identity_provider_grants_no_cross_origin_access(string path)
    {
        await using var factory = CreateFactory(enableCors: false);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(path, "POST"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            $"{path} must not be readable from another origin unless the application opts in");
    }

    [Theory]
    [InlineData("/spark/types")]
    [InlineData("/spark/translations")]
    [InlineData("/spark/auth/capabilities")]
    public async Task Opting_in_does_not_grant_cross_origin_access_to_the_rest_of_spark(string path)
    {
        await using var factory = CreateFactory(enableCors: true);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(path));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            $"opting in covers the OIDC protocol endpoints; it must not make {path} readable from "
            + "another origin — that was a bare pipeline-wide UseCors, and it is the defect this pins");
    }

    /// <summary>
    /// The control: opting in must still reach the endpoints that need it, or the two facts above
    /// would be indistinguishable from deleting CORS support altogether.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/connect/token", "POST")]
    [InlineData("/connect/userinfo")]
    public async Task Opting_in_allows_the_protocol_endpoints_a_browser_client_calls(
        string path, string method = "GET")
    {
        await using var factory = CreateFactory(enableCors: true);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(path, method));

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue(
            $"{path} is called cross-origin by every browser-based OIDC client; opting in has to work "
            + "or the option is decoration");
        allowed!.Should().Contain(HostileOrigin);
    }
}
