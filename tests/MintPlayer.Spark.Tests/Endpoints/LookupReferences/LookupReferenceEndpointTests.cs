using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Endpoints.PersistentObject;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.LookupReferences;

/// <summary>
/// Endpoint tests for /spark/lookupref/* — list, get, add, update, delete. The endpoints
/// are thin shims over <see cref="ILookupReferenceService"/>; we stub the service and
/// drive each endpoint's status-code translation, antiforgery enforcement, and
/// InvalidOperationException → 400 mapping.
/// </summary>
public class LookupReferenceEndpointTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private SparkEndpointFactory CreateFactory(ILookupReferenceService stub)
    {
        return new SparkEndpointFactory(
            Store,
            [TestModels.Person(PersonTypeId)],
            services =>
            {
                services.RemoveAll<ILookupReferenceService>();
                services.AddSingleton(stub);
            });
    }

    // --- list -----------------------------------------------------------

    [Fact]
    public async Task List_returns_200_with_items_from_the_service()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.GetAllAsync().Returns(Task.FromResult<IEnumerable<LookupReferenceListItem>>(
        [
            new LookupReferenceListItem { Name = "CarBrand", IsTransient = false, ValueCount = 3 },
            new LookupReferenceListItem { Name = "ColorScheme", IsTransient = true, ValueCount = 5 },
        ]));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/spark/lookupref/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<LookupReferenceListItem>>(JsonOpts);
        items.Should().NotBeNull();
        items!.Select(i => i.Name).Should().BeEquivalentTo(["CarBrand", "ColorScheme"]);
    }

    // --- get ------------------------------------------------------------

    [Fact]
    public async Task Get_returns_200_with_payload_when_found()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.GetAsync("CarBrand").Returns(new LookupReferenceDto
        {
            Name = "CarBrand", IsTransient = false,
            Values = [new LookupReferenceValueDto { Key = "BMW", Values = TranslatedString.Create("BMW") }],
        });
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/spark/lookupref/CarBrand");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<LookupReferenceDto>(JsonOpts);
        dto!.Name.Should().Be("CarBrand");
        dto.Values.Should().ContainSingle().Which.Key.Should().Be("BMW");
    }

    [Fact]
    public async Task Get_returns_404_when_not_found()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.GetAsync("Missing").Returns((LookupReferenceDto?)null);
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/spark/lookupref/Missing");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("error").GetString().Should().Contain("Missing");
    }

    // --- get through a readable type (#453) ------------------------------

    /// <summary>
    /// A type whose <c>Brand</c> attribute is bound to the CarBrand lookup. No CLR type, so no actions
    /// class, so granting it to anonymous needs no row rule (startup verifies one for a real entity).
    /// </summary>
    private static EntityTypeFile ProbeBindingCarBrand() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = Guid.Parse("aaaaaaaa-4530-4530-4530-aaaaaaaaaaaa"),
            Name = "LookupProbe",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Brand", DataType = "string", LookupReferenceType = "CarBrand" },
            ],
        },
    };

    private SparkEndpointFactory CreateFactory(ILookupReferenceService stub, SparkTestSecurity security) => new(
        Store,
        [ProbeBindingCarBrand()],
        services =>
        {
            services.RemoveAll<ILookupReferenceService>();
            services.AddSingleton(stub);
        },
        security: security);

    private static ILookupReferenceService CarBrandAndColorStub()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.GetAsync("CarBrand").Returns(new LookupReferenceDto { Name = "CarBrand", IsTransient = false });
        stub.GetAsync("ColorScheme").Returns(new LookupReferenceDto { Name = "ColorScheme", IsTransient = false });
        return stub;
    }

    /// <summary>
    /// A caller who may read Person must be able to render it, labels included — without holding
    /// <c>Read/LookupReferences</c>. The public-repository page bounced anonymous visitors to
    /// sign-in over exactly this.
    /// </summary>
    [Fact]
    public async Task Get_is_allowed_for_a_lookup_bound_by_a_type_the_caller_may_read()
    {
        await using var factory = CreateFactory(CarBrandAndColorStub(), SparkTestSecurity.Empty.Granting("Read/LookupProbe"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/spark/lookupref/CarBrand");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "LookupProbe binds CarBrand and the caller may Read it");
    }

    [Fact]
    public async Task Get_is_refused_for_a_lookup_no_readable_type_binds()
    {
        // Not a wholesale opening: ColorScheme is bound by nothing the caller may read.
        await using var factory = CreateFactory(CarBrandAndColorStub(), SparkTestSecurity.Empty.Granting("Read/LookupProbe"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/spark/lookupref/ColorScheme");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_is_refused_for_a_bound_lookup_when_the_binding_type_is_not_readable()
    {
        await using var factory = CreateFactory(CarBrandAndColorStub(), SparkTestSecurity.Empty.Granting("Query/LookupProbe"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/spark/lookupref/CarBrand");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK, "Query/LookupProbe is not Read/LookupProbe");
    }

    // --- add (POST) -----------------------------------------------------

    [Fact]
    public async Task AddValue_returns_201_with_created_value_on_success()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.AddValueAsync("CarBrand", Arg.Any<LookupReferenceValueDto>())
            .Returns(ci => Task.FromResult(ci.Arg<LookupReferenceValueDto>()));
        await using var factory = CreateFactory(stub);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var newValue = new LookupReferenceValueDto { Key = "Audi", Values = TranslatedString.Create("Audi") };
        var response = await client.SendAsync(HttpMethod.Post, "/spark/lookupref/CarBrand", JsonContent.Create(newValue, options: JsonOpts), requiresAntiforgery: true);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var echoed = await response.Content.ReadFromJsonAsync<LookupReferenceValueDto>(JsonOpts);
        echoed!.Key.Should().Be("Audi");
    }

    [Fact]
    public async Task AddValue_returns_400_when_service_throws_InvalidOperation()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.AddValueAsync("CarBrand", Arg.Any<LookupReferenceValueDto>())
            .Returns<LookupReferenceValueDto>(_ => throw new InvalidOperationException("Lookup 'CarBrand' is transient"));
        await using var factory = CreateFactory(stub);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var response = await client.SendAsync(
            HttpMethod.Post, "/spark/lookupref/CarBrand",
            JsonContent.Create(new LookupReferenceValueDto { Key = "Audi", Values = TranslatedString.Create("Audi") }, options: JsonOpts),
            requiresAntiforgery: true);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        // R2-M1: ex.Message no longer flows to the response body.
        body.GetProperty("error").GetString().Should().Be("Operation failed");
    }

    [Fact]
    public async Task AddValue_rejects_request_without_antiforgery_token()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient(); // no antiforgery cookie

        var response = await client.PostAsJsonAsync("/spark/lookupref/CarBrand",
            new LookupReferenceValueDto { Key = "Audi", Values = TranslatedString.Create("Audi") });

        // Antiforgery middleware blocks before the handler runs.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
        await stub.DidNotReceive().AddValueAsync(Arg.Any<string>(), Arg.Any<LookupReferenceValueDto>());
    }

    /// <summary>
    /// A body the add and update endpoints cannot bind answers what it answered while they read it by
    /// hand (measured before they became typed endpoints, endpoints generator completion M3). A caller
    /// without <c>Edit/LookupReferences</c> is refused before the body matters, as before.
    /// </summary>
    [Fact]
    public async Task AddValue_and_UpdateValue_answer_unbindable_bodies_as_before()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        await using var factory = CreateFactory(stub);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        await using var deniedFactory = CreateFactory(stub, SparkTestSecurity.Empty);
        using var denied = new SparkClient(deniedFactory.CreateClient(), ownsClient: true);

        static async Task<string> Probe(SparkClient client, HttpMethod method, string url)
        {
            var lines = new List<string>();
            foreach (var (label, content) in UnbindableBodies())
                lines.Add(await Line(label, () => client.SendAsync(method, url, content, requiresAntiforgery: true)));
            return string.Join("\n", lines);
        }

        var all = string.Join("\n",
            "# add", await Probe(client, HttpMethod.Post, "/spark/lookupref/CarBrand"),
            "# update", await Probe(client, HttpMethod.Put, "/spark/lookupref/CarBrand/BMW"),
            "# add, denied", await Probe(denied, HttpMethod.Post, "/spark/lookupref/CarBrand"),
            "# update, denied", await Probe(denied, HttpMethod.Put, "/spark/lookupref/CarBrand/BMW"));

        // Measured on the hand-read endpoints first. Identical, except "empty" and "malformed", which
        // escaped as an unhandled JsonException (a 500) and now answer the same 400 as "none".
        all.Should().Be("""
            # add
            none: 400 {"error":"Operation failed"}
            empty: 400 {"error":"Operation failed"}
            null: 400 {"error":"Invalid request body"}
            malformed: 400 {"error":"Operation failed"}
            text/plain: 400 {"error":"Operation failed"}
            # update
            none: 400 {"error":"Operation failed"}
            empty: 400 {"error":"Operation failed"}
            null: 400 {"error":"Invalid request body"}
            malformed: 400 {"error":"Operation failed"}
            text/plain: 400 {"error":"Operation failed"}
            # add, denied
            none: 404 {"error":"Not found"}
            empty: 404 {"error":"Not found"}
            null: 404 {"error":"Not found"}
            malformed: 404 {"error":"Not found"}
            text/plain: 404 {"error":"Not found"}
            # update, denied
            none: 404 {"error":"Not found"}
            empty: 404 {"error":"Not found"}
            null: 404 {"error":"Not found"}
            malformed: 404 {"error":"Not found"}
            text/plain: 404 {"error":"Not found"}
            """.ReplaceLineEndings("\n"));

        await stub.DidNotReceive().AddValueAsync(Arg.Any<string>(), Arg.Any<LookupReferenceValueDto>());
        await stub.DidNotReceive().UpdateValueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LookupReferenceValueDto>());
    }

    /// <summary>Sends, and records an exception escaping the server as <c>throws T</c>.</summary>
    internal static async Task<string> Line(string label, Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            var response = await send();
            return $"{label}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
        }
        catch (Exception ex)
        {
            return $"{label}: throws {ex.GetType().Name}";
        }
    }

    /// <summary>The bodies no endpoint can bind: none, empty, a JSON <c>null</c>, malformed JSON, the wrong content type.</summary>
    internal static IEnumerable<(string Label, HttpContent? Content)> UnbindableBodies()
    {
        yield return ("none", null);
        yield return ("empty", new StringContent("", System.Text.Encoding.UTF8, "application/json"));
        yield return ("null", new StringContent("null", System.Text.Encoding.UTF8, "application/json"));
        yield return ("malformed", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        yield return ("text/plain", new StringContent("{}", System.Text.Encoding.UTF8, "text/plain"));
    }

    // --- update (PUT) ---------------------------------------------------

    [Fact]
    public async Task UpdateValue_returns_200_with_updated_value_on_success()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.UpdateValueAsync("CarBrand", "BMW", Arg.Any<LookupReferenceValueDto>())
            .Returns(ci => Task.FromResult(ci.Arg<LookupReferenceValueDto>()));
        await using var factory = CreateFactory(stub);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var update = new LookupReferenceValueDto
        {
            Key = "BMW",
            Values = TranslatedString.Create("Bayerische Motoren Werke"),
            IsActive = false,
        };
        var response = await client.SendAsync(HttpMethod.Put, "/spark/lookupref/CarBrand/BMW", JsonContent.Create(update, options: JsonOpts), requiresAntiforgery: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var echoed = await response.Content.ReadFromJsonAsync<LookupReferenceValueDto>(JsonOpts);
        echoed!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateValue_returns_400_when_service_throws_InvalidOperation()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.UpdateValueAsync("CarBrand", "Ghost", Arg.Any<LookupReferenceValueDto>())
            .Returns<LookupReferenceValueDto>(_ => throw new InvalidOperationException("Key 'Ghost' not found"));
        await using var factory = CreateFactory(stub);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var response = await client.SendAsync(
            HttpMethod.Put, "/spark/lookupref/CarBrand/Ghost",
            JsonContent.Create(new LookupReferenceValueDto { Key = "Ghost", Values = TranslatedString.Create("x") }, options: JsonOpts),
            requiresAntiforgery: true);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- delete ---------------------------------------------------------

    [Fact]
    public async Task DeleteValue_returns_204_on_success()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.DeleteValueAsync("CarBrand", "BMW").Returns(Task.CompletedTask);
        await using var factory = CreateFactory(stub);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var response = await client.SendAsync(HttpMethod.Delete, "/spark/lookupref/CarBrand/BMW", requiresAntiforgery: true);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await stub.Received().DeleteValueAsync("CarBrand", "BMW");
    }

    [Fact]
    public async Task DeleteValue_returns_400_when_service_throws_InvalidOperation()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.DeleteValueAsync("CarBrand", "Ghost")
            .Returns(_ => throw new InvalidOperationException("Key 'Ghost' not found"));
        await using var factory = CreateFactory(stub);
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);

        var response = await client.SendAsync(HttpMethod.Delete, "/spark/lookupref/CarBrand/Ghost", requiresAntiforgery: true);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
