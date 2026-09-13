using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Who gets <c>Access-Control-Allow-Origin</c> by default, and who has to ask.
/// </summary>
/// <remarks>
/// <para>
/// Three tiers, and they differ on purpose:
/// </para>
/// <list type="bullet">
/// <item><b>Spark's own endpoints</b> (under the Spark prefix) answer with the policy by default. They
/// are a public, credential-free API; a caller with <c>curl</c> could already read the anonymous view,
/// so withholding the header only blocked browsers from something no other client was blocked from.</item>
/// <item><b>A library's endpoints outside that prefix</b> — the identity provider's <c>/connect</c> —
/// get nothing by default and opt in with <c>RequireCors</c>.</item>
/// <item><b>The application's own endpoints</b> likewise, except that an application registering its
/// own <i>default</i> policy still gets it: that is the app saying "everywhere".</item>
/// </list>
/// <para>
/// ⚠️ <b>What this does not expose.</b> A cross-origin request carries no cookies unless the response
/// grants credentials, and the policy is <c>AllowAnyOrigin</c> — which ASP.NET refuses to combine with
/// <c>AllowCredentials</c>. So a browser page reads the anonymous view and nothing else, and the
/// combination that would change that cannot be configured without confronting the refusal.
/// </para>
/// <para>
/// ⚠️ The case that genuinely changes with this default is a Spark app on a <b>private network</b>: a
/// public page a user visits can read its anonymous surface through their browser, which it could not
/// reach on its own. An intranet deployment that cares turns the policy off per endpoint with
/// <c>DisableCors</c>, which the last fact here pins.
/// </para>
/// </remarks>
public class SparkCorsDefaultTests : SparkTestDriver
{
    private const string Origin = "https://elsewhere.test";

    private static HttpRequestMessage Preflight(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }

    [Theory]
    [InlineData("/spark")]
    [InlineData("/spark/types")]
    [InlineData("/spark/queries")]
    [InlineData("/spark/aliases")]
    public async Task Sparks_own_endpoints_answer_cross_origin_by_default(string path)
    {
        await using var factory = new SparkEndpointFactory<TestSparkContext>(Store, models: []);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(path));

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue(
            $"{path} is part of Spark's own API and answers with the policy without anyone opting in");
        allowed!.Should().Contain("*",
            "the wildcard is the honest signal for a credential-free public read — and ASP.NET refuses "
            + "AllowCredentials alongside it, so this cannot later be widened into cross-origin access "
            + "to a signed-in user's data without someone confronting that refusal");
    }

    /// <summary>
    /// The opt-out, which is what makes the default safe to have: an endpoint under the Spark prefix
    /// that does not want the header says so, and the branch's policy yields to its metadata.
    /// </summary>
    [Fact]
    public async Task An_endpoint_under_the_spark_prefix_can_opt_out()
    {
        await using var factory = new SparkEndpointFactory<TestSparkContext>(
            Store,
            models: [],
            configureSpark: spark => spark.Registry.AddEndpoints(
                endpoints => endpoints.MapGet("/spark/no-cors-here", () => "ok").WithMetadata(new Microsoft.AspNetCore.Cors.DisableCorsAttribute())));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/spark/no-cors-here"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "DisableCors on the endpoint must beat the branch's policy, or a library or application "
            + "endpoint mounted under /spark could never decline the default");
    }

    [Fact]
    public async Task An_endpoint_outside_the_spark_prefix_gets_nothing_by_default()
    {
        await using var factory = new SparkEndpointFactory<TestSparkContext>(
            Store,
            models: [],
            configureSpark: spark => spark.Registry.AddEndpoints(
                endpoints => endpoints.MapGet("/elsewhere", () => "ok")));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/elsewhere"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "a library's or an application's own endpoints opt IN with RequireCors; the Spark default "
            + "belongs to Spark's API, not to everything the host happens to serve");
    }

    [Fact]
    public async Task An_endpoint_outside_the_spark_prefix_can_opt_in()
    {
        await using var factory = new SparkEndpointFactory<TestSparkContext>(
            Store,
            models: [],
            configureSpark: spark => spark.Registry.AddEndpoints(
                endpoints => endpoints.MapGet("/elsewhere-cors", () => "ok")
                    .RequireCors(SparkExtensions.SparkCorsPolicy)));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("/elsewhere-cors"));

        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue(
            "the policy is named and public so a library can reuse it rather than inventing its own");
        allowed!.Should().Contain("*");
    }
}
