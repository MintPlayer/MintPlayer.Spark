using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Refresh;

/// <summary>
/// The Fleet <c>triggersRefresh</c> sample, end to end against the real application: the real model
/// file, the real <c>CarActions.OnRefreshAsync</c>, the real endpoint, and the real antiforgery and
/// authorization stack.
///
/// <para>
/// The unit and integration suites cover this behaviour against fixtures they define themselves,
/// which means none of them can catch a model file that was never given the flag, an actions class
/// the resolver does not find, or an endpoint the route table does not expose. That gap is what this
/// is for.
/// </para>
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class TriggersRefreshTests
{
    private const string CarTypeId = "facb6829-f2a1-4ae2-a046-6ba506e8c0ce";

    private readonly FleetE2ECollectionFixture _fixture;
    public TriggersRefreshTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A cookie-bearing client with the antiforgery token primed, mirroring
    /// <c>LookupReferenceAuthTests</c>. The refresh endpoint carries
    /// <c>RequireAntiforgeryTokenAttribute</c> like every other mutating Spark endpoint, so a raw
    /// POST is answered 400 by the middleware and never reaches authorization.
    /// </summary>
    private (HttpClient Http, CookieContainer Cookies) CreateClient()
    {
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = true,
            CookieContainer = cookies,
        };
        return (new HttpClient(handler) { BaseAddress = new Uri(_fixture.Host.FleetUrl) }, cookies);
    }

    /// <summary>
    /// Primes and reads the antiforgery token.
    /// <para>
    /// ⚠️ Must be called <b>after</b> any sign-in. The token is bound to the identity it was issued
    /// to, so one primed while anonymous is rejected once the caller logs in — and the rejection is
    /// a bare 400 from the middleware, indistinguishable from sending no token at all.
    /// </para>
    /// </summary>
    private async Task<string> PrimeXsrfAsync(HttpClient http, CookieContainer cookies)
    {
        var warmup = await http.GetAsync("/spark/types");
        warmup.EnsureSuccessStatusCode();

        var value = cookies.GetCookies(new Uri(_fixture.Host.FleetUrl))["XSRF-TOKEN"]?.Value
            ?? throw new InvalidOperationException("No XSRF-TOKEN cookie issued");

        return Uri.UnescapeDataString(value);
    }

    private async Task SignInAsync(HttpClient http)
    {
        var login = await http.PostAsJsonAsync(
            "/spark/auth/login?useCookies=true",
            new { email = _fixture.Host.AdminEmailAddress, password = _fixture.Host.AdminPass });

        login.StatusCode.Should().Be(HttpStatusCode.OK,
            $"login should succeed. Body: {await login.Content.ReadAsStringAsync()}");
    }

    private static object CarPayload(string status) => new
    {
        persistentObject = new
        {
            name = "Car",
            objectTypeId = CarTypeId,
            attributes = new object[]
            {
                new { name = "Status", value = status },
                new { name = "LicensePlate", value = "1-ABC-123" },
                new { name = "Model", value = "Focus" },
                new { name = "Year", value = 2020 },
            },
        },
        triggeredBy = "Status",
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> RefreshAsync(string status, bool signIn = true)
    {
        var (http, cookies) = CreateClient();
        using var _ = http;
        if (signIn) await SignInAsync(http);
        var xsrfToken = await PrimeXsrfAsync(http, cookies);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/spark/po/{CarTypeId}/refresh")
        {
            Content = JsonContent.Create(CarPayload(status)),
        };
        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);

        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode,
            string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static JsonElement Attribute(JsonElement body, string name) =>
        body.GetProperty("result").GetProperty("attributes")
            .EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == name);

    [Fact]
    public async Task Marking_a_car_stolen_reshapes_the_form()
    {
        var (status, body) = await RefreshAsync("Stolen");

        status.Should().Be(HttpStatusCode.OK);

        Attribute(body, "PoliceReportNumber").GetProperty("isVisible").GetBoolean()
            .Should().BeTrue("a stolen car needs a police report");
        Attribute(body, "PoliceReportNumber").GetProperty("isRequired").GetBoolean()
            .Should().BeTrue();
        Attribute(body, "LicensePlate").GetProperty("isReadOnly").GetBoolean()
            .Should().BeTrue("this is the 'locks the vehicle record' the save prompt already promised");
        Attribute(body, "PromoVideoUrl").GetProperty("isVisible").GetBoolean()
            .Should().BeFalse("you do not advertise a car you no longer have");
    }

    [Fact]
    public async Task Leaving_stolen_restores_the_form()
    {
        // The idempotency half, and the reason the hook sets both sides of every flag. A hook that
        // only ever turns things on leaves a form permanently locked after one stray selection —
        // and because the same rules are re-derived on save, that is a record that cannot be saved.
        var (status, body) = await RefreshAsync("InUse");

        status.Should().Be(HttpStatusCode.OK);

        Attribute(body, "PoliceReportNumber").GetProperty("isVisible").GetBoolean().Should().BeFalse();
        Attribute(body, "PoliceReportNumber").GetProperty("isRequired").GetBoolean().Should().BeFalse();
        Attribute(body, "LicensePlate").GetProperty("isReadOnly").GetBoolean().Should().BeFalse();
        Attribute(body, "PromoVideoUrl").GetProperty("isVisible").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_without_an_antiforgery_token_is_rejected()
    {
        var (http, _) = CreateClient();
        using var owned = http;
        await SignInAsync(http);

        var response = await http.PostAsJsonAsync($"/spark/po/{CarTypeId}/refresh", CarPayload("Stolen"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_is_refused_for_an_anonymous_caller()
    {
        // The token is present, so this clears the antiforgery gate and is refused by authorization
        // proper — the stronger result. Accepting a 400 here would let the test pass on the
        // antiforgery gate alone and never exercise the right at all.
        var (status, _) = await RefreshAsync("Stolen", signIn: false);

        status.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }
}
