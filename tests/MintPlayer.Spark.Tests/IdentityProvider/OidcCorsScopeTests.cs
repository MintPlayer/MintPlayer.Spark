using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// A module declares what its own endpoints need, and reaches no further.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>The identity provider once reached much further.</b> <c>EnableDynamicCors</c> defaulted to
/// <see langword="true"/> with a <c>SetIsOriginAllowed(_ =&gt; true)</c> policy, applied by a bare
/// <c>app.UseCors("SparkOidcCors")</c> — pipeline-wide. So an application that turned the identity
/// provider on also let any page on any origin read the anonymous view of <c>/spark/types</c>,
/// <c>/spark/translations</c>, <c>/spark/permissions/*</c> and <c>/spark/auth/capabilities</c>, with no
/// <c>Access-Control-Allow-Credentials</c> — never the caller's own data, but a surface nobody asked
/// for and nothing documented. It now defaults to off, and opts its five protocol endpoints in
/// individually.
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
    /// The default: enabling the identity provider grants no cross-origin access to its own endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ The protocol endpoints are in this list deliberately. A cross-origin SPA client is a real
    /// scenario, but it is not the built-in one — Spark serves an application's own Angular frontend
    /// from the same host, so it never needed CORS. Granting it by default was paying for a scenario
    /// nobody in this repository has, with a permission the caller did not ask for.
    /// </para>
    /// <para>
    /// ⚠️ Every probe is <b>outside</b> the Spark prefix, and that is not an oversight. Spark's own
    /// endpoints answer cross-origin by default (see <c>SparkCorsDefaultTests</c>), so <c>/spark/*</c>
    /// can no longer tell "the identity provider leaked its policy" apart from "Spark's own policy
    /// applied, correctly". An earlier version of this fixture probed <c>/spark/types</c>; left alone it
    /// would have gone on passing for the wrong reason.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/connect/token")]
    [InlineData("/connect/authorize")]
    public async Task By_default_the_identity_provider_grants_no_cross_origin_access(string path)
    {
        await using var factory = CreateFactory(enableCors: false);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(path, "POST"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            $"{path} must not be readable from another origin unless the application opts in");
    }

    /// <summary>
    /// Opting in covers the five protocol endpoints and nothing else — not even their siblings.
    /// </summary>
    /// <remarks>
    /// These paths are in the same <c>/connect</c> group as the endpoints that did opt in, which is
    /// what makes them the right probe: a policy applied to the group, or to the pipeline, would reach
    /// them too. The original defect was a bare <c>app.UseCors("SparkOidcCors")</c>, and this is what
    /// fails if anything like it comes back.
    /// </remarks>
    [Theory]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/consent")]
    [InlineData("/connect/applications")]
    public async Task Opting_in_covers_only_the_endpoints_that_asked(string path)
    {
        await using var factory = CreateFactory(enableCors: true);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(path));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            $"{path} is reached by top-level navigation, never by fetch — it did not ask for CORS and "
            + "must not receive it because a sibling did");
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
    /// ⚠️ Asserted with the identity provider's CORS switched <b>off</b>, so no module has registered
    /// anything. Spark itself registers <c>SparkCorsPolicy</c>, so a request that merely succeeds is
    /// the proof: the middleware resolved its services and ran. Had <c>AddCors</c> been missing,
    /// <c>UseCors</c> would throw at startup and no request would be served at all.
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
    /// Spark's own policy wins on Spark's own endpoints, whatever the application defaults to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>This reverses an earlier decision, and the reversal is the point.</b> When Spark's
    /// middleware carried no policy of its own, an application's <c>AddDefaultPolicy</c> reached
    /// <c>/spark/*</c> — and that was recorded as deliberate, on the reasoning that a default policy is
    /// the application saying "everywhere". Spark now applies its own named policy to its own prefix,
    /// so the application's default no longer reaches there: the wildcard below is Spark's, not the
    /// app's specific origin.
    /// </para>
    /// <para>
    /// That is the better outcome for a reason the first framing missed. Spark's endpoints have their
    /// own security model — <c>security.json</c>, antiforgery, per-handler permission checks — and
    /// what they expose cross-origin should not depend on a convenience default an application set for
    /// its own controllers, possibly without realising the framework API was in range.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Sparks_own_policy_beats_an_applications_default_on_spark_paths()
    {
        await using var factory = new SparkEndpointFactory<OidcTestContext>(
            Store,
            models: [],
            configureServices: services => services.AddCors(
                cors => cors.AddDefaultPolicy(policy => policy.WithOrigins(HostileOrigin))),
            environment: "Development");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/spark/types"));

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue();
        allowed!.Should().Contain("*",
            "Spark's endpoints answer with Spark's policy; an application's default governs the "
            + "application's own endpoints, and reaching into the framework's was never intended");
    }

    /// <summary>
    /// The other half: outside the Spark prefix, the application's default still governs.
    /// </summary>
    [Fact]
    public async Task An_applications_default_still_governs_its_own_endpoints()
    {
        await using var factory = new SparkEndpointFactory<OidcTestContext>(
            Store,
            models: [],
            configureServices: services => services.AddCors(
                cors => cors.AddDefaultPolicy(policy => policy.WithOrigins(HostileOrigin))),
            configureSpark: spark => spark.Registry.AddEndpoints(
                endpoints => endpoints.MapGet("/app-endpoint", () => "ok")),
            environment: "Development");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/app-endpoint"));

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue(
            "Spark scopes its policy to its own prefix; it must not swallow the application's default "
            + "for everything else the host serves");
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
