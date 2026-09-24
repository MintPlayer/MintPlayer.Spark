using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// The passkey HTTP surface: what is mounted, what refuses, and — mostly — what every refusal is
/// careful <em>not</em> to say. The endpoints that matter here are anonymous, so their failure
/// shapes are the security-relevant part.
/// </summary>
public class PasskeyEndpointTests : SparkTestDriver
{
    private async Task<IHost> StartAsync(
        SparkPasskeys passkeys,
        SparkLocalCredentials localCredentials = SparkLocalCredentials.Disabled)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.Configure<SparkAuthenticationOptions>(o =>
                    {
                        o.Passkeys = passkeys;
                        o.LocalCredentials = localCredentials;
                    });
                    services.AddAuthentication().AddCookie("GitHub", "GitHub", _ => { });
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>(localCredentials));
                }))
            .StartAsync();
    }

    private static HashSet<string> MappedRoutes(IHost host) =>
        host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(
        IHost host, string path, object? payload = null)
    {
        using var client = host.GetTestServer().CreateClient();
        var response = payload is null
            ? await client.PostAsync(path, null)
            : await client.PostAsJsonAsync(path, payload);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    #region Gating

    [Fact]
    public async Task Disabled_mounts_no_passkey_route()
    {
        using var host = await StartAsync(SparkPasskeys.Disabled);

        MappedRoutes(host).Where(r => r.Contains("passkey", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("a disabled surface must be absent from the route table, not 404-shadowed");
    }

    [Fact]
    public async Task Enabled_mounts_the_whole_surface()
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);
        var routes = MappedRoutes(host);

        routes.Should().Contain("/spark/auth/passkeys/creation-options");
        routes.Should().Contain("/spark/auth/passkeys");
        routes.Should().Contain("/spark/auth/passkeys/{id}");
        routes.Should().Contain("/spark/auth/passkeys/{id}/name");
        routes.Should().Contain("/spark/auth/passkeys/request-options");
        routes.Should().Contain("/spark/auth/passkeys/sign-in");
    }

    /// <summary>The capability is derived from the route table, so it cannot advertise a phantom.</summary>
    [Theory]
    [InlineData(SparkPasskeys.Disabled, false)]
    [InlineData(SparkPasskeys.Enabled, true)]
    public async Task Capabilities_report_the_passkey_surface(SparkPasskeys mode, bool expected)
    {
        using var host = await StartAsync(mode);
        using var client = host.GetTestServer().CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/spark/auth/capabilities");

        capabilities.GetProperty("passkeys").GetBoolean().Should().Be(expected);
    }

    #endregion

    #region Authentication and antiforgery

    [Theory]
    [InlineData("/spark/auth/passkeys/creation-options")]
    [InlineData("/spark/auth/passkeys")]
    public async Task Enrollment_requires_authentication(string path)
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);

        var (status, _) = await PostAsync(host, path, new { credentialJson = "{}" });

        status.Should().Be(HttpStatusCode.Unauthorized, "a passkey is added to an account that is already signed in");
    }

    [Fact]
    public async Task Authenticated_routes_carry_antiforgery_metadata()
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);

        var gated = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/spark/auth/passkeys", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation == true)
            .Select(e => e.RoutePattern.RawText!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        gated.Should().Contain("/spark/auth/passkeys/creation-options");
        gated.Should().Contain("/spark/auth/passkeys");
        gated.Should().Contain("/spark/auth/passkeys/{id}");
        gated.Should().Contain("/spark/auth/passkeys/{id}/name");
    }

    /// <summary>
    /// The two anonymous routes are exempt for the same reason <c>/login</c> is: there is no session
    /// to protect yet, and requiring a token would make sign-in impossible for a fresh visitor.
    /// </summary>
    [Theory]
    [InlineData("/spark/auth/passkeys/request-options")]
    [InlineData("/spark/auth/passkeys/sign-in")]
    public async Task Sign_in_routes_are_reachable_anonymously(string path)
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);

        var (status, _) = await PostAsync(host, path, new { credentialJson = "{}" });

        status.Should().NotBe(HttpStatusCode.NotFound, "the route must exist when passkeys are enabled");
        status.Should().NotBe(HttpStatusCode.Forbidden, "a visitor signing in has no session yet");
    }

    #endregion

    #region The failure-mode requirements

    /// <summary>
    /// AC5 / D10. The request-options response must not vary with who is asking, because varying is
    /// what makes it a user-existence oracle. It takes no username at all, so this pins that two
    /// unrelated callers get the same shape.
    /// </summary>
    [Fact]
    public async Task Request_options_do_not_disclose_whether_an_account_exists()
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);

        using (var store = new UserStore<SparkUser>(Store))
        {
            var user = new SparkUser { UserName = "known@example.com", Email = "known@example.com", NormalizedEmail = "KNOWN@EXAMPLE.COM" };
            (await store.CreateAsync(user, CancellationToken.None)).Succeeded.Should().BeTrue();
        }

        var (knownStatus, knownBody) = await PostAsync(host, "/spark/auth/passkeys/request-options?username=known@example.com");
        var (unknownStatus, unknownBody) = await PostAsync(host, "/spark/auth/passkeys/request-options?username=nobody@example.com");

        knownStatus.Should().Be(HttpStatusCode.OK);
        unknownStatus.Should().Be(HttpStatusCode.OK);

        // Challenges differ per call by design; everything that describes *which credentials exist*
        // must not.
        var known = JsonDocument.Parse(knownBody).RootElement;
        var unknown = JsonDocument.Parse(unknownBody).RootElement;

        known.GetProperty("allowCredentials").GetArrayLength().Should().Be(0);
        unknown.GetProperty("allowCredentials").GetArrayLength().Should().Be(0);
        known.GetProperty("userVerification").GetString()
            .Should().Be(unknown.GetProperty("userVerification").GetString());
    }

    /// <summary>AC16 / R2 — asserted on the wire, not inferred from the framework default.</summary>
    [Fact]
    public async Task Request_options_require_user_verification()
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);

        var (_, body) = await PostAsync(host, "/spark/auth/passkeys/request-options");

        JsonDocument.Parse(body).RootElement.GetProperty("userVerification").GetString()
            .Should().Be("required", "under 'preferred' an authenticator may skip the PIN and the passkey becomes possession-only");
    }

    /// <summary>AC17 / R3 — malformed input is a 400 with no exception text.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"truncated\":")]
    [InlineData("{}")]
    public async Task Malformed_credential_json_is_a_bad_request(string credentialJson)
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);

        var (status, body) = await PostAsync(host, "/spark/auth/passkeys/sign-in", new { credentialJson });

        status.Should().BeOneOf([HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized],
            "a malformed ceremony payload is the caller's fault, never a 500");
        body.Should().NotContain("Exception", "an exception type in the body is an information leak");
        body.Should().NotContain("at MintPlayer.", "a stack frame in the body is an information leak");
    }

    /// <summary>AC18 / R4 — the failure modes must be indistinguishable.</summary>
    [Fact]
    public async Task Sign_in_failures_look_identical()
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);

        var (firstStatus, firstBody) = await PostAsync(host, "/spark/auth/passkeys/sign-in", new { credentialJson = "{\"id\":\"AAAA\"}" });
        var (secondStatus, secondBody) = await PostAsync(host, "/spark/auth/passkeys/sign-in", new { credentialJson = "{\"id\":\"BBBB\"}" });

        firstStatus.Should().Be(secondStatus);
        firstBody.Should().Be(secondBody, "distinguishing 'unknown credential' from 'bad signature' reintroduces the oracle D10 removes");
    }

    /// <summary>AC6 / AC15 — an attestation with no ceremony behind it cannot be evaluated.</summary>
    [Fact]
    public async Task Attestation_without_a_ceremony_is_refused()
    {
        using var host = await StartAsync(SparkPasskeys.Enabled);

        var (status, body) = await PostAsync(host, "/spark/auth/passkeys/sign-in", new { credentialJson = "{\"id\":\"AAAA\",\"response\":{}}" });

        status.Should().BeOneOf([HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized]);
        body.Should().NotContain("challenge", "the response must not echo ceremony state back to the caller");
    }

    #endregion
}
