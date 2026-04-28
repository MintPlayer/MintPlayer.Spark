using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
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

    // --- add (POST) -----------------------------------------------------

    [Fact]
    public async Task AddValue_returns_201_with_created_value_on_success()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.AddValueAsync("CarBrand", Arg.Any<LookupReferenceValueDto>())
            .Returns(ci => Task.FromResult(ci.Arg<LookupReferenceValueDto>()));
        await using var factory = CreateFactory(stub);
        using var client = await factory.CreateAuthorizedClientAsync();

        var newValue = new LookupReferenceValueDto { Key = "Audi", Values = TranslatedString.Create("Audi") };
        var response = await client.PostJsonAsync("/spark/lookupref/CarBrand", newValue);

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
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await client.PostJsonAsync("/spark/lookupref/CarBrand",
            new LookupReferenceValueDto { Key = "Audi", Values = TranslatedString.Create("Audi") });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("error").GetString().Should().Contain("transient");
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

    // --- update (PUT) ---------------------------------------------------

    [Fact]
    public async Task UpdateValue_returns_200_with_updated_value_on_success()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.UpdateValueAsync("CarBrand", "BMW", Arg.Any<LookupReferenceValueDto>())
            .Returns(ci => Task.FromResult(ci.Arg<LookupReferenceValueDto>()));
        await using var factory = CreateFactory(stub);
        using var client = await factory.CreateAuthorizedClientAsync();

        var update = new LookupReferenceValueDto
        {
            Key = "BMW",
            Values = TranslatedString.Create("Bayerische Motoren Werke"),
            IsActive = false,
        };
        var response = await client.PutJsonAsync("/spark/lookupref/CarBrand/BMW", update);

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
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await client.PutJsonAsync("/spark/lookupref/CarBrand/Ghost",
            new LookupReferenceValueDto { Key = "Ghost", Values = TranslatedString.Create("x") });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- delete ---------------------------------------------------------

    [Fact]
    public async Task DeleteValue_returns_204_on_success()
    {
        var stub = Substitute.For<ILookupReferenceService>();
        stub.DeleteValueAsync("CarBrand", "BMW").Returns(Task.CompletedTask);
        await using var factory = CreateFactory(stub);
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await client.DeleteAsync("/spark/lookupref/CarBrand/BMW");

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
        using var client = await factory.CreateAuthorizedClientAsync();

        var response = await client.DeleteAsync("/spark/lookupref/CarBrand/Ghost");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
