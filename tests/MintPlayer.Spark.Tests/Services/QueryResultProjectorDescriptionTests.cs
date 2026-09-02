using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using System.Text.Json;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #348 — a query column carries the attribute's <c>description</c> so the grid header can show
/// the same [i] tooltip the form shows. Columns are the only wire shape that needs it: the form,
/// detail page and AsDetail tables all read the entity-type schema directly.
/// </summary>
public class QueryResultProjectorDescriptionTests
{
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static EntityTypeDefinition Definition(TranslatedString? description) => new()
    {
        Id = Guid.Parse("dddd3333-3333-3333-3333-dddd33333333"),
        Name = "Probe",
        Attributes =
        [
            new EntityAttributeDefinition
            {
                Id = Guid.Parse("dddd4444-4444-4444-4444-dddd44444444"),
                Name = "Title",
                Label = TranslatedString.Create("Title"),
                Description = description,
                ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
            },
        ],
    };

    [Fact]
    public void Column_carries_the_attributes_description()
    {
        var description = TranslatedString.Create("What the title is for.", nl: "Waarvoor de titel dient.");

        var column = QueryResultProjector.BuildColumns(Definition(description)).Single();

        column.Description.Should().BeSameAs(description);
    }

    [Fact]
    public void Column_without_a_description_serialises_none()
    {
        var column = QueryResultProjector.BuildColumns(Definition(null)).Single();

        var json = JsonSerializer.Serialize(column, WireOptions);

        json.Should().NotContain("description");
    }

    [Fact]
    public void Column_description_serialises_as_a_translated_string()
    {
        var column = QueryResultProjector.BuildColumns(
            Definition(TranslatedString.Create("Help", nl: "Hulp"))).Single();

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(column, WireOptions));
        var description = json.RootElement.GetProperty("description");

        description.GetProperty("en").GetString().Should().Be("Help");
        description.GetProperty("nl").GetString().Should().Be("Hulp");
    }
}
