using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.RowLifecycle;

/// <summary>
/// The server-side row lifecycle (#386) against the real Fleet application: the real
/// <c>ServiceEntry.json</c> with its <c>serverSideRowLifecycle</c> flag, the real
/// <c>ServiceEntryActions</c>, the real endpoints, and the real antiforgery and authorization stack.
/// </summary>
/// <remarks>
/// This suite exists because of what the unit suites structurally cannot see. They construct the
/// wire object themselves, so a green run proves nothing about a model file that was never given the
/// flag, an actions class the resolver does not find, or a route the table does not expose — the
/// exact lesson the row-identity PRD records as W1, where ten passing tests covered a path that
/// carried no key.
/// <para>
/// The two assertions that matter most are the ones a hand-built object cannot make: that the
/// constructed row comes back <b>keyed</b> (a keyless row is invisible in memory, because the field
/// initializer mints a fresh guid on every load), and that removing an invoiced entry is refused by
/// the row type's own hook rather than by anything the client decided.
/// </para>
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class ServerSideRowLifecycleTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ServerSideRowLifecycleTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

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

    /// <summary>⚠️ Must be called after sign-in — a token issued while anonymous is rejected once the caller logs in.</summary>
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

    /// <summary>
    /// The ServiceEntry type id, read from the live catalogue rather than a generated constant.
    /// </summary>
    /// <remarks>
    /// Deliberate: <c>ServiceEntry.json</c> is written by the model synchronizer during the Fleet
    /// build, so binding to <c>PersistentObjectIds.Default.ServiceEntry</c> would make this test
    /// project fail to compile on any clean tree where Fleet has not been built yet. Reading it back
    /// also proves the type reaches the catalogue at all, which is half of what an E2E is for.
    /// </remarks>
    private static async Task<string> ServiceEntryTypeIdAsync(HttpClient http)
    {
        var types = await http.GetFromJsonAsync<JsonElement>("/spark/types");
        var entry = types.EnumerateArray()
            .FirstOrDefault(t => t.TryGetProperty("name", out var n) && n.GetString() == "ServiceEntry");

        entry.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "ServiceEntry must be in the catalogue for an administrator — security.json grants "
            + "QueryReadEditNewDelete/ServiceEntry");

        return entry.GetProperty("id").GetString()!;
    }

    /// <summary>Creates a car with two service entries, one of them invoiced, and returns its id.</summary>
    /// <remarks>
    /// ⚠️ <paramref name="entryTypeId"/> is not optional decoration: a nested AsDetail row on the wire
    /// carries a <b>required</b> <c>objectTypeId</c>, and omitting it fails deserialization before any
    /// endpoint code runs — a bare 500 with an empty body, which reads like a bug in the feature
    /// rather than in the payload.
    /// </remarks>
    private async Task<string> CreateCarWithEntriesAsync(HttpClient http, string xsrfToken, string entryTypeId)
    {
        var payload = new
        {
            persistentObject = new
            {
                name = "Car",
                objectTypeId = CarFixture.TypeId,
                attributes = new object[]
                {
                    new { name = "LicensePlate", value = CarFixture.RandomLicensePlate() },
                    new { name = "Model", value = "Focus" },
                    new { name = "Year", value = 2020 },
                    new
                    {
                        name = "ServiceEntries",
                        dataType = "AsDetail",
                        isArray = true,
                        asDetailType = "Fleet.Entities.ServiceEntry",
                        isValueChanged = true,
                        objects = new object[]
                        {
                            NewEntryPayload(entryTypeId, "Oil change", invoiced: false),
                            NewEntryPayload(entryTypeId, "Timing belt", invoiced: true),
                        },
                    },
                },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"/spark/po/{CarFixture.TypeId}")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);

        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        // Create answers 201, not 200 — asserting OK here failed on a car that had been created
        // perfectly well, with the success envelope right there in the message.
        response.IsSuccessStatusCode.Should().BeTrue(
            $"creating the car should succeed. Status: {(int)response.StatusCode}. Body: {text} "
            + $"| Fleet log: {_fixture.Host.RecentLog()}");

        return JsonDocument.Parse(text).RootElement.GetProperty("result").GetProperty("id").GetString()!;
    }

    private static object NewEntryPayload(string entryTypeId, string description, bool invoiced) => new
    {
        name = "ServiceEntry",
        objectTypeId = entryTypeId,
        attributes = new object[]
        {
            new { name = "Description", value = description, isValueChanged = true },
            new { name = "PerformedOn", value = "2026-01-15", isValueChanged = true },
            new { name = "Odometer", value = 120000, isValueChanged = true },
            new { name = "Cost", value = 249.50m, isValueChanged = true },
            new { name = "IsInvoiced", value = invoiced, isValueChanged = true },
        },
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        HttpClient http, string xsrfToken, string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
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
    public async Task A_new_row_arrives_with_the_hook_s_defaults_applied()
    {
        var (http, cookies) = CreateClient();
        using var _ = http;
        await SignInAsync(http);
        var xsrf = await PrimeXsrfAsync(http, cookies);

        var entryTypeId = await ServiceEntryTypeIdAsync(http);
        var carId = await CreateCarWithEntriesAsync(http, xsrf, entryTypeId);

        var (status, body) = await PostAsync(http, xsrf, $"/spark/po/{entryTypeId}/new", new
        {
            asDetailAttribute = "ServiceEntries",
            parentType = CarFixture.TypeId.ToString(),
            parentId = carId,
        });

        status.Should().Be(HttpStatusCode.OK);

        Attribute(body, "PerformedOn").GetProperty("value").GetString()
            .Should().StartWith(DateTime.Today.ToString("yyyy-MM-dd"),
                "OnNewAsync defaults the date to today");

        Attribute(body, "Description").GetProperty("value").GetString()
            .Should().StartWith("Service — ",
                "the hook reads the plate off AsDetailParent, so the parent really was loaded");
    }

    [Fact]
    public async Task A_new_row_arrives_keyed()
    {
        var (http, cookies) = CreateClient();
        using var _ = http;
        await SignInAsync(http);
        var xsrf = await PrimeXsrfAsync(http, cookies);

        var entryTypeId = await ServiceEntryTypeIdAsync(http);
        var carId = await CreateCarWithEntriesAsync(http, xsrf, entryTypeId);

        var (status, body) = await PostAsync(http, xsrf, $"/spark/po/{entryTypeId}/new", new
        {
            asDetailAttribute = "ServiceEntries",
            parentType = CarFixture.TypeId.ToString(),
            parentId = carId,
        });

        status.Should().Be(HttpStatusCode.OK);

        // The assertion no in-memory test can make. A keyless row deserialises with a freshly minted
        // guid, so it looks keyed everywhere except on the wire — which is the only place that
        // matters, because the client sends this back as __sparkRowKey and the save matches on it.
        body.GetProperty("result").GetProperty("id").GetString()
            .Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_new_row_defaults_do_not_arrive_marked_changed()
    {
        var (http, cookies) = CreateClient();
        using var _ = http;
        await SignInAsync(http);
        var xsrf = await PrimeXsrfAsync(http, cookies);

        var entryTypeId = await ServiceEntryTypeIdAsync(http);
        var carId = await CreateCarWithEntriesAsync(http, xsrf, entryTypeId);

        var (_, body) = await PostAsync(http, xsrf, $"/spark/po/{entryTypeId}/new", new
        {
            asDetailAttribute = "ServiceEntries",
            parentType = CarFixture.TypeId.ToString(),
            parentId = carId,
        });

        // SetOriginalValue, not SetValue. Otherwise clicking Add and clicking away raises an
        // unsaved-changes prompt over a car nobody edited.
        Attribute(body, "PerformedOn").GetProperty("isValueChanged").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Removing_an_invoiced_row_is_refused_with_a_readable_reason()
    {
        var (http, cookies) = CreateClient();
        using var _ = http;
        await SignInAsync(http);
        var xsrf = await PrimeXsrfAsync(http, cookies);

        var entryTypeId = await ServiceEntryTypeIdAsync(http);
        var carId = await CreateCarWithEntriesAsync(http, xsrf, entryTypeId);
        var invoicedKey = await RowKeyAsync(http, carId, "Timing belt");

        var (status, body) = await PostAsync(http, xsrf, $"/spark/po/{entryTypeId}/delete-row", new
        {
            asDetailAttribute = "ServiceEntries",
            parentType = CarFixture.TypeId.ToString(),
            parentId = carId,
            rowKey = invoicedKey,
        });

        status.Should().Be(HttpStatusCode.BadRequest,
            "a hook's refusal must be a message the user can read, not a discarded save");

        body.GetProperty("result").GetProperty("errors")[0]
            .GetProperty("errorMessage").GetProperty("en").GetString()
            .Should().Contain("already been invoiced");
    }

    [Fact]
    public async Task Removing_an_ordinary_row_is_allowed()
    {
        var (http, cookies) = CreateClient();
        using var _ = http;
        await SignInAsync(http);
        var xsrf = await PrimeXsrfAsync(http, cookies);

        var entryTypeId = await ServiceEntryTypeIdAsync(http);
        var carId = await CreateCarWithEntriesAsync(http, xsrf, entryTypeId);
        var openKey = await RowKeyAsync(http, carId, "Oil change");

        var (status, _) = await PostAsync(http, xsrf, $"/spark/po/{entryTypeId}/delete-row", new
        {
            asDetailAttribute = "ServiceEntries",
            parentType = CarFixture.TypeId.ToString(),
            parentId = carId,
            rowKey = openKey,
        });

        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_row_key_the_parent_does_not_hold_is_refused_without_saying_so()
    {
        var (http, cookies) = CreateClient();
        using var _ = http;
        await SignInAsync(http);
        var xsrf = await PrimeXsrfAsync(http, cookies);

        var entryTypeId = await ServiceEntryTypeIdAsync(http);
        var carId = await CreateCarWithEntriesAsync(http, xsrf, entryTypeId);

        var (status, _) = await PostAsync(http, xsrf, $"/spark/po/{entryTypeId}/delete-row", new
        {
            asDetailAttribute = "ServiceEntries",
            parentType = CarFixture.TypeId.ToString(),
            parentId = carId,
            rowKey = Guid.NewGuid().ToString("N"),
        });

        // Refused identically to an unknown type, so the endpoint cannot be used to ask which keys
        // exist — the same anti-oracle rule the rest of the framework's refusals follow.
        status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Both_endpoints_require_an_antiforgery_token()
    {
        var (http, cookies) = CreateClient();
        using var _ = http;
        await SignInAsync(http);
        var xsrf = await PrimeXsrfAsync(http, cookies);
        var entryTypeId = await ServiceEntryTypeIdAsync(http);
        var carId = await CreateCarWithEntriesAsync(http, xsrf, entryTypeId);

        var body = new
        {
            asDetailAttribute = "ServiceEntries",
            parentType = CarFixture.TypeId.ToString(),
            parentId = carId,
            rowKey = "anything",
        };

        (await http.PostAsJsonAsync($"/spark/po/{entryTypeId}/new", body))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await http.PostAsJsonAsync($"/spark/po/{entryTypeId}/delete-row", body))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_anonymous_caller_reaches_neither_endpoint()
    {
        var (adminHttp, adminCookies) = CreateClient();
        using var _ = adminHttp;
        await SignInAsync(adminHttp);
        var adminXsrf = await PrimeXsrfAsync(adminHttp, adminCookies);
        var entryTypeId = await ServiceEntryTypeIdAsync(adminHttp);
        var carId = await CreateCarWithEntriesAsync(adminHttp, adminXsrf, entryTypeId);

        var (http, cookies) = CreateClient();
        using var owned = http;
        var xsrf = await PrimeXsrfAsync(http, cookies);

        var body = new
        {
            asDetailAttribute = "ServiceEntries",
            parentType = CarFixture.TypeId.ToString(),
            parentId = carId,
            rowKey = "anything",
        };

        // The token is present, so this clears the antiforgery gate and is refused by authorization
        // proper. Accepting a 400 would let the test pass on the gate alone.
        var (newStatus, _) = await PostAsync(http, xsrf, $"/spark/po/{entryTypeId}/new", body);
        var (deleteStatus, _) = await PostAsync(http, xsrf, $"/spark/po/{entryTypeId}/delete-row", body);

        newStatus.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
        deleteStatus.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    /// <summary>The stored row key of the entry whose description matches.</summary>
    private async Task<string> RowKeyAsync(HttpClient http, string carId, string description)
    {
        var car = await http.GetFromJsonAsync<JsonElement>(
            $"/spark/po/{CarFixture.TypeId}/{Uri.EscapeDataString(carId)}");

        var entries = car.GetProperty("attributes").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "ServiceEntries")
            .GetProperty("objects");

        var row = entries.EnumerateArray().Single(o =>
            o.GetProperty("attributes").EnumerateArray()
                .Single(a => a.GetProperty("name").GetString() == "Description")
                .GetProperty("value").GetString() == description);

        return row.GetProperty("id").GetString()!;
    }
}
