using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
/// SP1 and SP3 of the #439 passkey plan, run against Spark's real auth wiring.
///
/// SP1 asks whether <see cref="SignInManager{TUser}"/>'s passkey helpers work at all here. They park
/// the ceremony state in a DataProtection-protected cookie under
/// <c>IdentityConstants.TwoFactorUserIdScheme</c> — but Spark registers Identity through
/// <c>AddIdentityApiEndpoints</c>, not <c>AddIdentity</c>, and the two register different scheme
/// sets. If that scheme is missing the first call throws at runtime rather than startup. The whole
/// design (PRD D1) rests on the answer, because the alternative is hand-writing a data-protection
/// wrapper for state that ships as plaintext JSON.
///
/// SP3 asks which store methods the handler reaches for, and therefore whether
/// <c>IUserPasskeyStore</c> sits on the sign-in hot path.
/// </summary>
public class PasskeyCeremonyStateTests : SparkTestDriver
{
    private const string NoCeremonyMarker = "no passkey attestation is underway";

    private async Task<IHost> StartAsync()
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();

                    // Disabled keeps the MapIdentityApi surface minimal; the ceremony helpers are
                    // called directly below, so no local-credential route is involved.
                    services.AddAuthentication().AddCookie("GitHub", "GitHub", _ => { });
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapSparkIdentityApi<SparkUser>(SparkLocalCredentials.Disabled);

                        // Stand-ins for the endpoints M4/M5 will add. They exist only to drive the
                        // SignInManager helpers inside a real request.
                        endpoints.MapPost("/spike/creation-options", async (HttpContext ctx, SignInManager<SparkUser> signInManager, string userId) =>
                        {
                            var entity = new PasskeyUserEntity
                            {
                                Id = userId,
                                Name = "spike@example.com",
                                DisplayName = "Spike User",
                            };

                            try
                            {
                                return Results.Text(await signInManager.MakePasskeyCreationOptionsAsync(entity), "application/json");
                            }
                            catch (Exception ex)
                            {
                                return Results.Text($"THREW {ex.GetType().Name}: {ex.Message}", "text/plain", statusCode: 500);
                            }
                        });

                        // Posts deliberately malformed credential JSON: the ceremony can never
                        // succeed, so the *reason* it fails is the measurement. "No attestation is
                        // underway" means the state was not retrieved; anything else means it was.
                        endpoints.MapPost("/spike/attestation", async (SignInManager<SparkUser> signInManager) =>
                        {
                            try
                            {
                                var result = await signInManager.PerformPasskeyAttestationAsync("{}");
                                return Results.Text($"RESULT succeeded={result.Succeeded} failure={result.Failure?.Message}", "text/plain");
                            }
                            catch (Exception ex)
                            {
                                return Results.Text($"THREW {ex.GetType().Name}: {ex.Message}", "text/plain");
                            }
                        });
                    });
                }))
            .StartAsync();
    }

    private static async Task<(HttpResponseMessage Response, string Body)> PostAsync(
        IHost host, string path, string? cookie = null, bool https = false)
    {
        using var client = host.GetTestServer().CreateClient();
        if (https)
            client.BaseAddress = new Uri("https://localhost");

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    private static string? CeremonyCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(".AspNetCore.Identity.TwoFactorUserId", StringComparison.OrdinalIgnoreCase)
                                      || v.Contains("TwoFactorUserId", StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// SP1, the gate. A non-existent user id keeps the store out of the picture, so this measures
    /// state storage and nothing else.
    /// </summary>
    [Fact]
    public async Task MakePasskeyCreationOptions_emits_the_ceremony_cookie()
    {
        using var host = await StartAsync();

        var (response, body) = await PostAsync(host, "/spike/creation-options?userId=Users/does-not-exist");

        body.Should().NotStartWith("THREW", "SP1 fails if SignInManager cannot store its ceremony state under Spark's wiring");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("challenge", "the options JSON is what the browser needs");

        CeremonyCookie(response).Should().NotBeNull("the ceremony state must be parked server-side, not handed to the client");
    }

    /// <summary>
    /// R5 — the cookie's own attributes. Same-origin SPA, so Lax is sufficient; Secure and HttpOnly
    /// are not.
    /// </summary>
    [Fact]
    public async Task Ceremony_cookie_is_httponly_and_secure()
    {
        using var host = await StartAsync();

        // Over HTTPS, which is what production serves. Measured on an http request the Secure flag
        // is absent, which is SameAsRequest behaving correctly rather than a gap — asserting it
        // over http would pin the wrong contract.
        var (response, _) = await PostAsync(host, "/spike/creation-options?userId=Users/does-not-exist", https: true);
        var cookie = CeremonyCookie(response);

        cookie.Should().NotBeNull();

        var attributes = cookie!.ToLowerInvariant();
        attributes.Should().Contain("httponly", "the ceremony state must be unreadable from script");
        attributes.Should().Contain("secure", "over HTTPS the ceremony cookie must be marked Secure");
        attributes.Should().Contain("samesite", "the ceremony cookie must state a SameSite policy rather than inherit the browser default");
    }

    /// <summary>
    /// The negative half of SP1: without the cookie the framework must refuse, and it must refuse
    /// with the "nothing underway" reason rather than a verification failure. This is what makes the
    /// positive case below meaningful.
    /// </summary>
    [Fact]
    public async Task Attestation_without_the_cookie_reports_no_ceremony()
    {
        using var host = await StartAsync();

        var (_, body) = await PostAsync(host, "/spike/attestation");

        body.ToLowerInvariant().Should().Contain(NoCeremonyMarker,
            "with no ceremony cookie the framework cannot have retrieved any state");
    }

    /// <summary>
    /// The positive half of SP1: carrying the cookie back must change the failure reason. The
    /// attestation still fails — the credential JSON is nonsense — but it must fail for a different
    /// reason, which proves the state round-tripped through the cookie.
    /// </summary>
    [Fact]
    public async Task Attestation_with_the_cookie_retrieves_the_state()
    {
        using var host = await StartAsync();

        var (optionsResponse, _) = await PostAsync(host, "/spike/creation-options?userId=Users/does-not-exist");
        var cookie = CeremonyCookie(optionsResponse);
        cookie.Should().NotBeNull();

        var cookieHeader = cookie!.Split(';')[0];
        var (_, body) = await PostAsync(host, "/spike/attestation", cookieHeader);

        body.ToLowerInvariant().Should().NotContain(NoCeremonyMarker,
            "the cookie carries the ceremony state, so the framework must get past the 'nothing underway' check");
    }

    /// <summary>
    /// SP3, measured 2026-09-24 and rewritten by M2 as planned. The handler resolves the user and
    /// then asks for their existing passkeys, to populate <c>excludeCredentials</c> so the same
    /// authenticator cannot enroll twice. Before M2 this threw
    /// <c>NotSupportedException: Store does not implement IUserPasskeyStore&lt;TUser&gt;</c>, which
    /// is what established that the store sits on the enrollment path rather than behind it. Note
    /// the contrast with the tests above, which pass a user id that does not resolve and therefore
    /// never reach the store at all.
    /// </summary>
    [Fact]
    public async Task Creation_options_for_a_real_user_consult_the_passkey_store()
    {
        using var host = await StartAsync();

        var user = new SparkUser { UserName = "spike@example.com", Email = "spike@example.com" };
        using (var scope = host.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
            var created = await userManager.CreateAsync(user);
            created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var (_, body) = await PostAsync(host, $"/spike/creation-options?userId={Uri.EscapeDataString(user.Id!)}");

        // If the handler consults the store, this throws NotSupportedException today, and the
        // assertion message carries the answer either way. M2 depends on knowing which.
        body.Should().NotStartWith("THREW", $"the store now implements IUserPasskeyStore — got: {body}");
        body.Should().Contain("excludeCredentials",
            "the handler asks the store for the user's existing passkeys, so the store is on the enrollment path");
    }
}
