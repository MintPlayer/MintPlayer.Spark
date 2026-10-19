using System.Text.Json.Nodes;
using MintPlayer.Spark.IdentityProvider.Migrations;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// <see cref="M_202610191200_OidcResourceTextEntries"/> (commerce X17, S24 M1) rewrites the texts of stored
/// <c>OidcResources</c> — production data in CodeCoverage and SeoTester — from the shape RavenDB persisted
/// before X17 to the one <c>TranslatedString</c> shape.
/// </summary>
/// <remarks>
/// The old documents are written through classes that reproduce the pre-X17 POCO serialization exactly
/// (<c>{"Translations":{…},"Key":null}</c>), with the <c>OidcResources</c> collection set in metadata, so the
/// migration runs against the shape production holds, not one rebuilt by the new converter. Red before the
/// migration: a typed load of such a document fails, because the store converter does not read the old form.
/// </remarks>
public class OidcResourceTextEntriesMigrationTests : SparkTestDriver
{
    private sealed class LegacyText
    {
        public Dictionary<string, string> Translations { get; set; } = new();
        public string? Key { get; set; }
    }

    private sealed class LegacyScope
    {
        public string Name { get; set; } = string.Empty;
        public LegacyText? DisplayName { get; set; }
        public LegacyText? Description { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private sealed class LegacyResource
    {
        public string Kind { get; set; } = OidcResourceKinds.Api;
        public string Name { get; set; } = string.Empty;
        public LegacyText? DisplayName { get; set; }
        public LegacyText? Description { get; set; }
        public List<LegacyScope> Scopes { get; set; } = [];
    }

    private static LegacyText Text(params (string Lang, string Value)[] texts)
        => new() { Translations = texts.ToDictionary(t => t.Lang, t => t.Value) };

    private async Task SeedLegacyAsync()
    {
        using var session = Store.OpenAsyncSession();
        var api = new LegacyResource
        {
            Name = "fleet",
            DisplayName = Text(("en", "Spark Fleet"), ("nl", "Spark Vloot")),
            Description = Text(("en", "The fleet API")),
            Scopes =
            [
                new LegacyScope { Name = "fleet.read", DisplayName = Text(("en", "Read the cars"), ("fr", "Lire les voitures")), Description = null },
                new LegacyScope { Name = "fleet.write", DisplayName = Text(("en", "Change the cars")), Description = Text(("en", "Write access")) },
            ],
        };
        await session.StoreAsync(api, "OidcResources/fleet");
        session.Advanced.GetMetadataFor(api)["@collection"] = "OidcResources";

        var identity = new LegacyResource { Kind = OidcResourceKinds.Identity, Name = "openid", DisplayName = Text(("en", "Your identity"), ("nl", "Je identiteit")) };
        await session.StoreAsync(identity, "OidcResources/openid");
        session.Advanced.GetMetadataFor(identity)["@collection"] = "OidcResources";

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
    public async Task The_old_shape_cannot_be_loaded_before_the_migration()
    {
        await SeedLegacyAsync();

        (await RawAsync("OidcResources/fleet"))["DisplayName"]!["Translations"].Should().NotBeNull("the seed is the pre-X17 shape");

        using var session = Store.OpenAsyncSession();
        var act = async () => await session.LoadAsync<OidcResource>("OidcResources/fleet");
        await act.Should().ThrowAsync<Exception>("no backward compatibility (K3): only the migration reads the old shape");
    }

    [Fact]
    public async Task Every_text_moves_to_the_entries_shape_and_loads()
    {
        await SeedLegacyAsync();

        await new M_202610191200_OidcResourceTextEntries(Store).UpAsync(CancellationToken.None);

        var raw = await RawAsync("OidcResources/fleet");
        raw["DisplayName"]!.ToJsonString().Should().Be("""{"Entries":[{"Tag":"en","Value":"Spark Fleet"},{"Tag":"nl","Value":"Spark Vloot"}]}""");
        raw["Description"]!.ToJsonString().Should().Be("""{"Entries":[{"Tag":"en","Value":"The fleet API"}]}""");
        raw["Scopes"]![0]!["DisplayName"]!.ToJsonString().Should().Be("""{"Entries":[{"Tag":"en","Value":"Read the cars"},{"Tag":"fr","Value":"Lire les voitures"}]}""");
        raw["Scopes"]![0]!["Description"].Should().BeNull("a missing text stays missing");
        raw["Scopes"]![1]!["Description"]!.ToJsonString().Should().Be("""{"Entries":[{"Tag":"en","Value":"Write access"}]}""");

        using var session = Store.OpenAsyncSession();
        var fleet = (await session.LoadAsync<OidcResource>("OidcResources/fleet"))!;
        fleet.DisplayName!.GetValue("nl").Should().Be("Spark Vloot");
        fleet.Scopes[0].DisplayName!.GetValue("fr").Should().Be("Lire les voitures");
        fleet.Scopes[1].Description!.GetValue("en").Should().Be("Write access");

        var openid = (await session.LoadAsync<OidcResource>("OidcResources/openid"))!;
        openid.DisplayName!.GetValue("nl-BE").Should().Be("Je identiteit");
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        await SeedLegacyAsync();
        var migration = new M_202610191200_OidcResourceTextEntries(Store);
        await migration.UpAsync(CancellationToken.None);
        var once = (await RawAsync("OidcResources/fleet")).ToJsonString();

        await migration.UpAsync(CancellationToken.None);

        (await RawAsync("OidcResources/fleet")).ToJsonString().Should().Be(once);
    }

    [Fact]
    public async Task A_resource_written_by_the_new_code_is_left_as_it_is()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new OidcResource
            {
                Id = "OidcResources/email",
                Kind = OidcResourceKinds.Identity,
                Name = "email",
                DisplayName = Abstractions.TranslatedString.Of(("en", "Your email address"), ("nl", "Je e-mailadres")),
            });
            await session.SaveChangesAsync();
        }
        var before = (await RawAsync("OidcResources/email"))["DisplayName"]!.ToJsonString();

        await new M_202610191200_OidcResourceTextEntries(Store).UpAsync(CancellationToken.None);

        (await RawAsync("OidcResources/email"))["DisplayName"]!.ToJsonString().Should().Be(before);
    }
}
