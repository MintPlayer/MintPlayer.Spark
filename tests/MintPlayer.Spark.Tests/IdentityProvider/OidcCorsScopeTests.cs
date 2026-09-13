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
    /// A Spark host registers the CORS middleware whether or not anything uses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes <c>RequireCors</c> safe for a module to write. ASP.NET fails a request whose
    /// endpoint carries CORS metadata when no CORS middleware is in the pipeline, so without a
    /// standing registration a module that declares what its endpoint needs, but does not also arrange
    /// the pipeline, turns "no CORS" into "this endpoint is dead". That is not hypothetical — it is
    /// what happened to <c>/connect/token</c>.
    /// </para>
    /// <para>
    /// The same arrangement as antiforgery, whose own registration in <c>UseSpark</c> carries the same
    /// note: keep the middleware present so endpoint metadata is always honourable.
    /// </para>
    /// <para>
    /// ⚠️ Asserted here by a host with the identity provider's CORS switched <b>off</b>, so nothing in
    /// the process has registered a policy. A request that merely succeeds proves it: the middleware is
    /// present, resolving its services, and granting nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_cors_middleware_is_registered_even_when_no_module_uses_it()
    {
        await using var factory = CreateFactory(enableCors: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/spark");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "UseSpark registers the CORS middleware unconditionally; if AddCors were missing, UseCors "
            + "would throw at startup and no request would be served at all");
    }

    /// <summary>
    /// An application's own default CORS policy reaches Spark's endpoints too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Recorded because it is the one way the standing <c>UseCors()</c> can widen anything.</b>
    /// Spark registers no policy of its own, so the middleware is inert — but <c>UseCors()</c> with no
    /// policy name applies the <i>default</i> policy when one exists, and an application that calls
    /// <c>AddDefaultPolicy</c> for its own controllers gets it on <c>/spark/*</c> as well.
    /// </para>
    /// <para>
    /// Left as-is deliberately: it is the application's own default, applied to the application's own
    /// endpoints, and an app that wants Spark excluded can name its policy instead of defaulting it.
    /// Forcing Spark's endpoints to opt out would override a choice the app made on purpose. This
    /// fact exists so the behaviour is a decision rather than a discovery.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_applications_own_default_cors_policy_also_applies_to_spark_endpoints()
    {
        await using var factory = new SparkEndpointFactory<OidcTestContext>(
            Store,
            models: [],
            configureServices: services => services.AddCors(
                cors => cors.AddDefaultPolicy(policy => policy.WithOrigins(HostileOrigin))),
            environment: "Development");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/spark/types"));

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue(
            "an application that registers a DEFAULT policy has asked for it everywhere, Spark "
            + "included — if this ever needs to stop, the fix is Spark naming its own policy, not "
            + "silently discarding the application's");
        allowed!.Should().Contain(HostileOrigin);
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
