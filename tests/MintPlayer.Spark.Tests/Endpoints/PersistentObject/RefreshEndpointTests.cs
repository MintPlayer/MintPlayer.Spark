using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents.Linq;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;
using POA = MintPlayer.Spark.Abstractions.PersistentObjectAttribute;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// The refresh endpoint, and the enforcement that makes it worth having.
///
/// <para>
/// The load-bearing test here is <see cref="Save_enforces_a_hook_imposed_rule_for_a_client_that_never_refreshed"/>.
/// Everything else could pass in a design where the server simply believes the metadata a client
/// posts — which would make the whole feature a decoration a hostile client steps around by never
/// calling <c>/refresh</c> at all.
/// </para>
/// </summary>
public class RefreshEndpointTests : SparkTestDriver
{
    private static readonly Guid CarTypeId = Guid.Parse("7e5f0000-0000-4000-8000-000000000001");

    private SparkEndpointFactory<RefreshTestContext> _factory = null!;
    private SparkClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory<RefreshTestContext>(Store, [RefreshTestModels.Car(CarTypeId)]);
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static PO Car(string? status, string? policeReport = null, string? id = null) => new()
    {
        Id = id,
        Name = "RefreshTestCar",
        ObjectTypeId = CarTypeId,
        Attributes =
        [
            new POA { Name = "Status", Value = status },
            new POA { Name = "PoliceReport", Value = policeReport },
            new POA { Name = "PromoUrl", Value = "https://example.test/promo" },
        ],
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostRefreshAsync(PO obj, string? triggeredBy)
    {
        var response = await _client.SendAsync(
            HttpMethod.Post,
            $"/spark/po/{CarTypeId}/refresh",
            JsonContent.Create(new { persistentObject = obj, triggeredBy }),
            requiresAntiforgery: true);

        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static JsonElement Attribute(JsonElement envelope, string name) =>
        envelope.GetProperty("result").GetProperty("attributes")
            .EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == name);

    // ---- the endpoint --------------------------------------------------------------------

    [Fact]
    public async Task Refresh_returns_the_object_reshaped_by_the_hook()
    {
        var (status, body) = await PostRefreshAsync(Car("Stolen"), "Status");

        status.Should().Be(HttpStatusCode.OK);
        Attribute(body, "PoliceReport").GetProperty("isRequired").GetBoolean().Should().BeTrue();
        Attribute(body, "PoliceReport").GetProperty("isVisible").GetBoolean().Should().BeTrue();
        Attribute(body, "PromoUrl").GetProperty("isVisible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_restores_the_shape_when_the_trigger_moves_back()
    {
        // The idempotency half. A hook that only ever adds is the easy half to write and the one
        // that leaves a form permanently locked after a stray selection.
        var (_, body) = await PostRefreshAsync(Car("InUse"), "Status");

        Attribute(body, "PoliceReport").GetProperty("isRequired").GetBoolean().Should().BeFalse();
        Attribute(body, "PoliceReport").GetProperty("isVisible").GetBoolean().Should().BeFalse();
        Attribute(body, "PromoUrl").GetProperty("isVisible").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_replaces_the_options_of_a_lookup_attribute()
    {
        var (_, body) = await PostRefreshAsync(Car("InMaintenance"), "Status");

        var options = Attribute(body, "Garage").GetProperty("options");
        options.EnumerateArray().Select(o => o.GetProperty("key").GetString())
            .Should().Equal(["garages/1", "garages/2"]);
    }

    [Fact]
    public async Task Refresh_leaves_options_null_when_the_hook_does_not_touch_them()
    {
        // null is "use your own source", not "no options". Emitting an empty array here would blank
        // every dropdown on the form on every refresh.
        var (_, body) = await PostRefreshAsync(Car("InUse"), "Status");

        Attribute(body, "Garage").GetProperty("options").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Refresh_ignores_metadata_submitted_by_the_client()
    {
        // The discriminator for building from the model rather than trusting the wire. The client
        // claims PromoUrl is required and visible; the hook says otherwise, and the hook must win —
        // as must the model, for anything the hook does not mention.
        var obj = Car("Stolen");
        obj["PromoUrl"].IsRequired = true;
        obj["PromoUrl"].IsVisible = true;
        obj["PoliceReport"].IsRequired = false;

        var (_, body) = await PostRefreshAsync(obj, "Status");

        Attribute(body, "PromoUrl").GetProperty("isVisible").GetBoolean().Should().BeFalse();
        Attribute(body, "PromoUrl").GetProperty("isRequired").GetBoolean().Should().BeFalse();
        Attribute(body, "PoliceReport").GetProperty("isRequired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_of_an_unknown_type_is_refused()
    {
        var response = await _client.SendAsync(
            HttpMethod.Post,
            $"/spark/po/{Guid.NewGuid()}/refresh",
            JsonContent.Create(new { persistentObject = Car("Stolen"), triggeredBy = "Status" }),
            requiresAntiforgery: true);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refresh_without_an_antiforgery_token_is_rejected()
    {
        using var bare = _factory.CreateClient();

        var response = await bare.PostAsJsonAsync(
            $"/spark/po/{CarTypeId}/refresh",
            new { persistentObject = Car("Stolen"), triggeredBy = "Status" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_trigger_name_is_not_an_error()
    {
        var (status, _) = await PostRefreshAsync(Car("Stolen"), "NoSuchAttribute");

        status.Should().Be(HttpStatusCode.OK);
    }

    // ---- enforcement ---------------------------------------------------------------------

    [Fact]
    public async Task Save_enforces_a_hook_imposed_rule_for_a_client_that_never_refreshed()
    {
        // ★ The criterion the feature lives or dies on. This request goes straight to Create — no
        // /refresh call was ever made, and the posted object claims nothing about requiredness. The
        // model does not mark PoliceReport required; only the hook does, and only when Status is
        // Stolen. If the server does not re-derive the rules itself, this save succeeds and the
        // whole feature is client-side decoration.
        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.CreatePersistentObjectAsync(Car("Stolen")));

        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ex.Message.Should().Contain("PoliceReport");
    }

    [Fact]
    public async Task Save_accepts_the_same_object_once_the_hook_imposed_rule_is_satisfied()
    {
        var created = await _client.CreatePersistentObjectAsync(Car("Stolen", policeReport: "PR-2026-1"));

        created.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Save_does_not_impose_the_rule_when_the_trigger_says_otherwise()
    {
        var created = await _client.CreatePersistentObjectAsync(Car("InUse"));

        created.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Save_accepts_a_value_violating_a_model_rule_the_hook_removed()
    {
        // The relaxing direction. The model puts maxLength 5 on Nickname; the hook lifts it when the
        // car is in maintenance. A design that only ever intersects rules — or that reads the model
        // for rules and the object for requiredness — fails here and passes everything above it.
        var obj = Car("InMaintenance");
        obj.Attributes.Should().NotBeNull();
        var withNickname = new PO
        {
            Name = obj.Name,
            ObjectTypeId = obj.ObjectTypeId,
            Attributes = [.. obj.Attributes, new POA { Name = "Nickname", Value = "a-long-nickname" }],
        };

        var created = await _client.CreatePersistentObjectAsync(withNickname);

        created.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Save_still_enforces_a_model_rule_the_hook_leaves_alone()
    {
        var obj = Car("InUse");
        var withNickname = new PO
        {
            Name = obj.Name,
            ObjectTypeId = obj.ObjectTypeId,
            Attributes = [.. obj.Attributes, new POA { Name = "Nickname", Value = "a-long-nickname" }],
        };

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => _client.CreatePersistentObjectAsync(withNickname));

        ex.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

// ---- fixtures ----------------------------------------------------------------------------

public sealed class RefreshTestCar
{
    public string? Id { get; set; }
    public string? Status { get; set; }
    public string? PoliceReport { get; set; }
    public string? PromoUrl { get; set; }
    public string? Garage { get; set; }
    public string? Nickname { get; set; }
}

public class RefreshTestContext : SparkContext
{
    public IRavenQueryable<RefreshTestCar> RefreshTestCars => Session.Query<RefreshTestCar>();
}

/// <summary>
/// The sample the demo will mirror: one status attribute reshaping the rest of the form, written so
/// that every branch fully re-establishes the state rather than patching the previous call's.
/// </summary>
public class RefreshTestCarActions : DefaultPersistentObjectActions<RefreshTestCar>
{
    public RefreshTestCarActions(MintPlayer.Spark.Services.IEntityMapper mapper) : base(mapper) { }

    public override Task OnRefreshAsync(SparkRefreshArgs<RefreshTestCar> args)
    {
        var obj = args.PersistentObject;
        var status = obj["Status"].Value?.ToString();

        var stolen = status == "Stolen";
        obj["PoliceReport"].IsRequired = stolen;
        obj["PoliceReport"].IsVisible = stolen;
        obj["PromoUrl"].IsVisible = !stolen;

        var maintenance = status == "InMaintenance";
        obj["Garage"].Options = maintenance
            ?
            [
                new PersistentObjectAttributeOption { Key = "garages/1", Label = TranslatedString.Create("North") },
                new PersistentObjectAttributeOption { Key = "garages/2", Label = TranslatedString.Create("South") },
            ]
            : null;

        // A rule the model declares, lifted while the car is off the road.
        obj["Nickname"].Rules = maintenance ? [] : obj["Nickname"].Rules;

        return Task.CompletedTask;
    }
}

public static class RefreshTestModels
{
    public static EntityTypeFile Car(Guid id) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = id,
            Name = "RefreshTestCar",
            ClrType = typeof(RefreshTestCar).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Status", DataType = "string", Order = 1,
                    TriggersRefresh = true,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "PoliceReport", DataType = "string", Order = 2,
                    IsVisible = false,
                },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "PromoUrl", DataType = "string", Order = 3 },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Garage", DataType = "LookupReference", Order = 4,
                    LookupReferenceType = "RefreshTestGarage",
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Nickname", DataType = "string", Order = 5,
                    Rules = [new ValidationRule { Type = "maxLength", Value = 5 }],
                },
            ],
        }
    };
}
