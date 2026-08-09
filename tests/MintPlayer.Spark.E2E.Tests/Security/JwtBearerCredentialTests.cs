using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// An OAuth2 <c>client_credentials</c> token used as an ordinary Spark credential.
/// <para>
/// This is the round trip D1 chose over a bespoke PAT library, and until now nothing joined its two
/// halves: <c>MintPlayer.Spark.IdentityProvider</c> could mint a machine token and no Spark app
/// could accept one, because no demo registered <c>AddJwtBearerCredential</c>. A token the framework
/// issued authenticated nothing.
/// </para>
/// <para>
/// Fleet plays both roles here — issuer and resource server. A real topology separates them (one
/// SparkId, many resource servers), but the two halves are configured independently either way, and
/// one host is what makes the round trip testable without a second deployment.
/// </para>
/// <para>
/// The issuer runs on <b>http</b> in tests. The JWT handler fetches the discovery document from the
/// issuer itself, so over https the host would have to trust its own development certificate — true
/// on a dev machine, not on a CI runner. Tokens are then presented over https like any other call.
/// </para>
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class JwtBearerCredentialTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public JwtBearerCredentialTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    /// <summary>Matches the audience Fleet's E2E configuration requires of every token it accepts.</summary>
    private const string Audience = "fleet-api";

    /// <summary>Granted <c>ReadEditNew/Car</c> in Fleet's security.json, exactly like a person's group.</summary>
    private const string MachineGroup = "Machine:FleetApi";

    private HttpClient NewHttpsClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { BaseAddress = new Uri(_fixture.Host.FleetUrl) };

    /// <summary>
    /// Completes a real <c>client_credentials</c> exchange against the running issuer and returns the
    /// access token. Deliberately not a hand-minted JWT: a token this test signed itself would prove
    /// the validation parameters and nothing about whether the two halves agree.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(string clientId, string secret, string scope)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.Host.FleetHttpUrl) };

        var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", secret),
            new KeyValuePair<string, string>("scope", scope),
        ]));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            // The issuer's own log, because a 500 here carries an empty body and the failure is
            // always server-side — a setup error reported without it reads as "the token endpoint
            // is broken" when it is usually the seeded client that is wrong.
            throw new InvalidOperationException(
                $"Token request failed ({(int)response.StatusCode}): {body}\n"
                + $"--- Fleet log ---\n{_fixture.Host.RecentLog(40)}");
        }

        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
    }

    private static object NewCarRequest()
        => new { persistentObject = CarFixture.New(CarFixture.RandomLicensePlate("JW")) };

    [Fact]
    public async Task A_client_credentials_token_authenticates_and_carries_its_security_json_rights()
    {
        var clientId = $"fleet-ci-{Guid.NewGuid():N}"[..20];
        var scope = $"api-{Guid.NewGuid():N}"[..12];
        var secret = await _fixture.Host.SeedMachineClientAsync(clientId, scope, Audience, MachineGroup);

        var token = await GetAccessTokenAsync(clientId, secret, scope);

        using var client = NewHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.PostAsJsonAsync($"/spark/po/{CarFixture.TypeId}", NewCarRequest());

        // Everything in one assertion, and every link was previously unexercised: the IdP issued a
        // machine token, the resource server validated it against the issuer's published keys, the
        // `group` claim survived (it was once emitted as `client_group`, matching nothing), and
        // security.json granted New/Car to that group. The POST also carries no XSRF token — a
        // bearer credential is not ambient and must be exempt.
        response.IsSuccessStatusCode.Should().BeTrue(
            "{0} is granted ReadEditNew/Car, so a token carrying that group may create one — got {1}.\n"
            + "--- Fleet log ---\n{2}",
            MachineGroup, response.StatusCode, _fixture.Host.RecentLog(30));
    }

    [Fact]
    public async Task A_token_for_a_different_audience_is_refused()
    {
        // The confused-deputy case, and the reason AddJwtBearerCredential refuses to start without
        // an Audience. This token is genuine — same issuer, same signing key, correct signature —
        // and was simply obtained for a different resource. Only the audience says otherwise.
        var clientId = $"other-{Guid.NewGuid():N}"[..20];
        var scope = $"other-{Guid.NewGuid():N}"[..12];
        var secret = await _fixture.Host.SeedMachineClientAsync(
            clientId, scope, audience: "some-other-api", group: MachineGroup);

        var token = await GetAccessTokenAsync(clientId, secret, scope);

        using var client = NewHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.PostAsJsonAsync($"/spark/po/{CarFixture.TypeId}", NewCarRequest());

        // 400, the anonymous-path signature: the token was refused, so the request carries no
        // credential and antiforgery answers first. Asserting the exact code rather than merely
        // "not success" keeps this from passing on an unrelated failure — and the sibling test
        // above, identical except for the audience, is what proves 400 here means the audience.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a signature proves who minted the token, not who it was meant for");
    }

    [Fact]
    public async Task A_garbage_bearer_token_is_refused_without_a_server_error()
    {
        using var client = NewHttpsClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");

        var response = await client.PostAsJsonAsync($"/spark/po/{CarFixture.TypeId}", NewCarRequest());

        response.IsSuccessStatusCode.Should().BeFalse();
        // A malformed credential is a refusal, not a fault. A 500 here would mean an unhandled
        // parse exception reaching the pipeline, which is both a leak and a denial-of-service knob.
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError,
            "an unparseable token must be refused, not throw");
    }
}
