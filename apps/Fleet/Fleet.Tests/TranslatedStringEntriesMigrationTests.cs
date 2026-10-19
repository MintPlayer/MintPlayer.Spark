using System.Text.Json.Nodes;
using Fleet.Entities;
using Fleet.Migrations;
using MintPlayer.Spark.Testing;

namespace Fleet.Tests;

/// <summary>
/// <see cref="M_202610191205_TranslatedStringEntries"/> (commerce X17, S24 M2 + M3): <c>Cars.Description</c>
/// and the dynamic lookup values move from the shapes RavenDB held before X17 to the one
/// <c>TranslatedString</c> shape, <c>{"Entries":[{"Tag":"en","Value":"…"}]}</c>.
/// </summary>
/// <remarks>
/// The old documents are written through classes that reproduce the pre-X17 serialization exactly, with the
/// collection set in metadata, so the migration runs against the real stored shapes. Red before the migration:
/// a typed load of such a car fails, because the store converter does not read the old form.
/// </remarks>
public class TranslatedStringEntriesMigrationTests : SparkTestDriver
{
    private sealed class LegacyText
    {
        public Dictionary<string, string> Translations { get; set; } = new();
        public string? Key { get; set; }
    }

    private sealed class LegacyCar
    {
        public string LicensePlate { get; set; } = string.Empty;
        public LegacyText? Description { get; set; }
    }

    private sealed class LegacyLookupValue
    {
        public string Key { get; set; } = string.Empty;
        public Dictionary<string, string> Translations { get; set; } = new();
        public bool IsActive { get; set; } = true;
        public Dictionary<string, object>? Extra { get; set; }
    }

    private sealed class LegacyLookup
    {
        public string Name { get; set; } = string.Empty;
        public List<LegacyLookupValue> Values { get; set; } = [];
    }

    private async Task SeedLegacyAsync()
    {
        using var session = Store.OpenAsyncSession();

        var described = new LegacyCar
        {
            LicensePlate = "1-ABC-123",
            Description = new LegacyText { Translations = { ["en"] = "A fast car", ["nl"] = "Een snelle auto" } },
        };
        await session.StoreAsync(described, "Cars/legacy-1");
        session.Advanced.GetMetadataFor(described)["@collection"] = "Cars";

        var plain = new LegacyCar { LicensePlate = "2-ABC-123", Description = null };
        await session.StoreAsync(plain, "Cars/legacy-2");
        session.Advanced.GetMetadataFor(plain)["@collection"] = "Cars";

        var brands = new LegacyLookup
        {
            Name = "CarBrand",
            Values =
            [
                new LegacyLookupValue { Key = "bmw", Translations = { ["en"] = "BMW", ["nl"] = "BMW" } },
                new LegacyLookupValue { Key = "vw", Translations = { ["en"] = "Volkswagen" }, IsActive = false },
            ],
        };
        await session.StoreAsync(brands, "LookupReferences/CarBrand");
        session.Advanced.GetMetadataFor(brands)["@collection"] = "DynamicLookupReferenceDocuments";

        await session.SaveChangesAsync();
    }

    private async Task<JsonNode> RawAsync(string id)
    {
        using var reader = Store.OpenAsyncSession();
        var command = new Raven.Client.Documents.Commands.GetDocumentsCommand(Store.Conventions, id, includes: null, metadataOnly: false);
        await reader.Advanced.RequestExecutor.ExecuteAsync(command, reader.Advanced.Context);
        return JsonNode.Parse(command.Result.Results[0]!.ToString()!)!;
    }

    [Fact]
    public async Task The_old_car_shape_cannot_be_loaded_before_the_migration()
    {
        await SeedLegacyAsync();

        using var session = Store.OpenAsyncSession();
        var act = async () => await session.LoadAsync<Car>("Cars/legacy-1");
        await act.Should().ThrowAsync<Exception>("no backward compatibility (K3): only the migration reads the old shape");
    }

    [Fact]
    public async Task Car_descriptions_and_lookup_values_move_to_the_entries_shape()
    {
        await SeedLegacyAsync();

        await new M_202610191205_TranslatedStringEntries(Store).UpAsync(CancellationToken.None);

        (await RawAsync("Cars/legacy-1"))["Description"]!.ToJsonString()
            .Should().Be("""{"Entries":[{"Tag":"en","Value":"A fast car"},{"Tag":"nl","Value":"Een snelle auto"}]}""");
        (await RawAsync("Cars/legacy-2"))["Description"].Should().BeNull();

        var lookup = await RawAsync("LookupReferences/CarBrand");
        var values = lookup["Values"]!.AsArray();
        values[0]!["Values"]!.ToJsonString().Should().Be("""{"Entries":[{"Tag":"en","Value":"BMW"},{"Tag":"nl","Value":"BMW"}]}""");
        values[0]!["Translations"].Should().BeNull("the old member is removed");
        values[1]!["Values"]!.ToJsonString().Should().Be("""{"Entries":[{"Tag":"en","Value":"Volkswagen"}]}""");
        values[1]!["IsActive"]!.GetValue<bool>().Should().BeFalse("the rest of the value is untouched");

        using var session = Store.OpenAsyncSession();
        var car = (await session.LoadAsync<Car>("Cars/legacy-1"))!;
        car.Description!.GetValue("nl").Should().Be("Een snelle auto");
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        await SeedLegacyAsync();
        var migration = new M_202610191205_TranslatedStringEntries(Store);
        await migration.UpAsync(CancellationToken.None);
        var car = (await RawAsync("Cars/legacy-1")).ToJsonString();
        var lookup = (await RawAsync("LookupReferences/CarBrand")).ToJsonString();

        await migration.UpAsync(CancellationToken.None);

        (await RawAsync("Cars/legacy-1")).ToJsonString().Should().Be(car);
        (await RawAsync("LookupReferences/CarBrand")).ToJsonString().Should().Be(lookup);
    }
}
