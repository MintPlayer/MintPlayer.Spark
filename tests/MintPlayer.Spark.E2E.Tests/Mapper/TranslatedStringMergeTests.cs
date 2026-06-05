using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Mapper;

/// <summary>
/// PRD §1 (inverse mapping) — the <c>TranslatedString</c> merge behavior must run
/// end-to-end: a PATCH-style update that carries only <c>{ en: "…" }</c> must preserve
/// the existing <c>fr</c> / <c>nl</c> entries rather than wiping them. The PRD originally
/// named HR Profession as the subject, but the E2E host is Fleet-only until §2 lands
/// the HR host fixture — so <c>Car.Description</c> (a new <see cref="TranslatedString"/>
/// field) carries the scenario on the existing Fleet harness.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class TranslatedStringMergeTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public TranslatedStringMergeTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Partial_update_preserves_languages_absent_from_incoming_dict()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var created = await client.CreatePersistentObjectAsync(CarWithDescription(
            CarFixture.RandomLicensePlate("TS"),
            ("en", "A fast sports car"),
            ("fr", "Une voiture de sport rapide"),
            ("nl", "Een snelle sportwagen")));
        created.Id.Should().NotBeNullOrEmpty(
            $"create must return id\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        var refetched = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Created car {created.Id} not re-fetchable");
        AssertTranslations(refetched, ("en", "A fast sports car"), ("fr", "Une voiture de sport rapide"), ("nl", "Een snelle sportwagen"));

        // PATCH-style update: only { en } — the server must merge, preserving fr + nl.
        var partialUpdate = CarFromExistingWithDescription(refetched, ("en", "A very fast sports car"));
        await client.UpdatePersistentObjectAsync(partialUpdate);

        var afterMerge = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Updated car {created.Id} not re-fetchable");
        AssertTranslations(afterMerge,
            ("en", "A very fast sports car"),
            ("fr", "Une voiture de sport rapide"),
            ("nl", "Een snelle sportwagen"));
    }

    [Fact]
    public async Task Full_update_overwrites_only_provided_languages()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var created = await client.CreatePersistentObjectAsync(CarWithDescription(
            CarFixture.RandomLicensePlate("TO"),
            ("en", "Original"),
            ("fr", "Original-FR")));
        var refetched = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Created car {created.Id} not re-fetchable");

        // Overwrite both languages with new values — confirms merge is incremental, not
        // whole-document replacement on update.
        var fullUpdate = CarFromExistingWithDescription(refetched, ("en", "Updated"), ("fr", "Updated-FR"));
        await client.UpdatePersistentObjectAsync(fullUpdate);

        var afterUpdate = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Updated car {created.Id} not re-fetchable");
        AssertTranslations(afterUpdate, ("en", "Updated"), ("fr", "Updated-FR"));
    }

    private static PersistentObject CarWithDescription(string plate, params (string Lang, string Value)[] entries)
        => new()
        {
            Name = CarFixture.TypeName,
            ObjectTypeId = CarFixture.TypeId,
            Attributes =
            [
                new PersistentObjectAttribute { Name = CarFixture.AttributeNames.LicensePlate, Value = plate },
                new PersistentObjectAttribute { Name = CarFixture.AttributeNames.Model,        Value = "TS1" },
                new PersistentObjectAttribute { Name = CarFixture.AttributeNames.Year,         Value = 2024 },
                new PersistentObjectAttribute
                {
                    Name = "Description",
                    DataType = "TranslatedString",
                    Value = entries.ToDictionary(e => e.Lang, e => e.Value),
                },
            ],
        };

    private static PersistentObject CarFromExistingWithDescription(PersistentObject existing, params (string Lang, string Value)[] entries)
    {
        var attrs = existing.Attributes
            .Where(a => a.Name != "Description")
            .Select(a => new PersistentObjectAttribute
            {
                Name = a.Name,
                Value = a.Value,
                DataType = a.DataType,
                IsValueChanged = a.IsValueChanged,
            })
            .Append(new PersistentObjectAttribute
            {
                Name = "Description",
                DataType = "TranslatedString",
                Value = entries.ToDictionary(e => e.Lang, e => e.Value),
                IsValueChanged = true,
            })
            .ToArray();

        return new PersistentObject
        {
            Id = existing.Id,
            Name = existing.Name,
            ObjectTypeId = existing.ObjectTypeId,
            Etag = existing.Etag,
            Attributes = attrs,
        };
    }

    private static void AssertTranslations(PersistentObject po, params (string Lang, string Value)[] expected)
    {
        var attr = po.Attributes.Single(a => a.Name == "Description");
        attr.Value.Should().NotBeNull("Description value must round-trip on the wire");
        var translations = ParseTranslations(attr.Value!);
        foreach (var (lang, value) in expected)
            translations.Should().ContainKey(lang).WhoseValue.Should().Be(value, $"language '{lang}' should be preserved");
    }

    private static Dictionary<string, string> ParseTranslations(object value)
    {
        // The server sends TranslatedString as a flat JSON object. Depending on where we
        // are in the client stack it may arrive as a JsonElement, a dict, or a string.
        if (value is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, string>();
            foreach (var prop in je.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString()!;
            }
            return result;
        }
        if (value is IDictionary<string, string> dict)
            return new Dictionary<string, string>(dict);
        if (value is IDictionary<string, object> objDict)
            return objDict.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
        throw new InvalidOperationException($"Unexpected Description wire shape: {value.GetType().FullName}");
    }
}
