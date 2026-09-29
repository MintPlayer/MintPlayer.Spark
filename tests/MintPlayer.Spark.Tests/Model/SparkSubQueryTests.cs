using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// #460 M15, D17/D19 — the shape of a sub-query entry and of a query's selection mode in the model
/// file, and the startup validation of <c>parentReference</c>.
/// </summary>
public class SparkSubQueryTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void A_bare_alias_and_an_object_entry_both_read()
    {
        var entries = JsonSerializer.Deserialize<SparkSubQuery[]>(
            """["company-cars", { "query": "company-people", "selectionMode": "single", "parentReference": "CompanyId" }]""",
            Options)!;

        entries[0].Query.Should().Be("company-cars");
        entries[0].SelectionMode.HasValue.Should().BeFalse();
        entries[0].IsBare.Should().BeTrue();
        entries[1].Query.Should().Be("company-people");
        entries[1].SelectionMode.Should().Be(SparkSelectionMode.Single);
        entries[1].ParentReference.Should().Be("CompanyId");
    }

    [Fact]
    public void An_entry_without_overrides_is_written_back_as_the_bare_alias()
    {
        SparkSubQuery[] entries = ["company-cars", new SparkSubQuery { Query = "company-people", SelectionMode = SparkSelectionMode.Multiple }];

        var json = JsonSerializer.Serialize(entries);

        json.Should().Be("""["company-cars",{"query":"company-people","selectionMode":"multiple"}]""");
    }

    [Fact]
    public void An_object_entry_without_a_query_is_refused()
    {
        var read = () => JsonSerializer.Deserialize<SparkSubQuery[]>("""[{ "selectionMode": "none" }]""", Options);
        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void Every_selection_mode_round_trips_in_lower_case()
    {
        foreach (var mode in Enum.GetValues<SparkSelectionMode>())
        {
            var json = JsonSerializer.Serialize(new SparkQuery { Id = Guid.NewGuid(), Name = "Q", Source = "Custom.Q", SelectionMode = mode });
            json.Should().Contain($"\"SelectionMode\":\"{mode.ToString().ToLowerInvariant()}\"");
            JsonSerializer.Deserialize<SparkQuery>(json, Options)!.SelectionMode.Should().Be(mode);
        }
    }

    [Fact]
    public void An_absent_selection_mode_is_null_meaning_auto()
    {
        var query = JsonSerializer.Deserialize<SparkQuery>("""{ "id": "46150000-0000-4000-8000-0000000000aa", "name": "Q", "source": "Custom.Q" }""", Options)!;
        query.SelectionMode.HasValue.Should().BeFalse();
    }

    // ---- parentReference validation -------------------------------------------------------------

    private static EntityTypeDefinition Type(string name, params EntityAttributeDefinition[] attributes) => new()
    {
        Id = Guid.NewGuid(), Name = name, ClrType = $"App.{name}", Attributes = attributes,
    };

    private static EntityAttributeDefinition Attr(string name, string dataType = "Reference", string? referenceType = "App.Company", bool isArray = false) => new()
    {
        Id = Guid.NewGuid(), Name = name, DataType = dataType, ReferenceType = referenceType, IsArray = isArray,
    };

    private static IReadOnlyList<string> Validate(string? entryReference, string? queryReference, params EntityAttributeDefinition[] carAttributes)
    {
        var company = Type("Company");
        company.Queries = [new SparkSubQuery { Query = "company-cars", ParentReference = entryReference }];
        var car = Type("Car", carAttributes);
        var query = new SparkQuery { Id = Guid.NewGuid(), Name = "Company_Cars", Alias = "company-cars", Source = "Custom.Company_Cars", EntityType = "Car", ParentReference = queryReference };
        return SparkSubQueries.ValidateParentReferences([company, car], [query]);
    }

    [Fact]
    public void A_parentReference_naming_a_reference_to_the_parent_is_valid()
    {
        Validate("OwnerId", null, Attr("OwnerId"), Attr("LessorId")).Should().BeEmpty();
        Validate(null, "LessorId", Attr("OwnerId"), Attr("LessorId")).Should().BeEmpty();
        Validate(null, null, Attr("OwnerId"), Attr("LessorId")).Should().BeEmpty("no name, nothing to validate");
    }

    [Fact]
    public void A_parentReference_naming_no_attribute_is_refused()
        => Validate("Missing", null, Attr("OwnerId")).Should().ContainSingle().Which.Should().Contain("not an attribute");

    [Fact]
    public void A_parentReference_naming_a_reference_to_another_type_is_refused()
        => Validate("DriverId", null, Attr("DriverId", referenceType: "App.Person")).Should().ContainSingle().Which.Should().Contain("not the parent's type");

    [Fact]
    public void A_parentReference_naming_a_scalar_or_an_array_is_refused()
    {
        Validate("Name", null, Attr("Name", dataType: "string", referenceType: null)).Should().ContainSingle();
        Validate("OwnerIds", null, Attr("OwnerIds", isArray: true)).Should().ContainSingle();
    }

    [Fact]
    public void The_entry_override_is_validated_before_the_query_default()
        => Validate("Missing", "OwnerId", Attr("OwnerId")).Should().ContainSingle().Which.Should().Contain("entry");

    [Fact]
    public void An_entry_names_its_query_by_alias_name_or_id()
    {
        var query = new SparkQuery { Id = Guid.NewGuid(), Name = "GetCars", Source = "Database.Cars" };
        SparkSubQueries.Names("cars", query).Should().BeTrue("the derived alias");
        SparkSubQueries.Names("GetCars", query).Should().BeTrue();
        SparkSubQueries.Names(query.Id.ToString(), query).Should().BeTrue();
        SparkSubQueries.Names("people", query).Should().BeFalse();
    }
}
