using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the <em>persisted</em> shape of a <see cref="TranslatedString"/>, which generated per-language index
/// fields depend on (commerce X17).
/// <para>
/// One shape for the API and the store: <c>{"Entries":[{"Tag":"nl","Value":"…"}]}</c>, optional members
/// omitted, no <c>Key</c>. The System.Text.Json converter writes it on the API; the Newtonsoft converter that
/// <c>ApplySparkConventions</c> registers writes the same bytes into RavenDB through the shared
/// <see cref="TranslatedStringJson"/> codec.
/// </para>
/// <para>
/// <strong>Why this test exists.</strong> The member names are the CLR names because RavenDB's LINQ
/// translator addresses stored JSON by CLR member name: a generated map reads
/// <c>x.Description.Entries.Where(e =&gt; e.Tag == "nl")</c>. Dropping the Newtonsoft converter (RavenDB would
/// then persist the POCO, nulls included) or renaming a member would silently empty every generated
/// per-language index field: no deploy failure, no index error, correct row counts, empty values.
/// Before X17 the store held <c>{"Translations":{…},"Key":null}</c> while the API sent <c>{"en":…}</c>.
/// </para>
/// </summary>
public class TranslatedStringPersistedShapeTests : SparkTestDriver
{
    public class Car
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public TranslatedString? Description { get; set; }
    }

    private static TranslatedString Multilingual() => new()
    {
        Entries =
        [
            new TranslatedStringEntry("en", "Alpha with spaces"),
            new TranslatedStringEntry("nl", "Zebra met spaties"),
            new TranslatedStringEntry("zh-Hant", "臺北")
            {
                Kind = TranslatedStringKind.Official,
                ValidFrom = new DateOnly(2020, 1, 1),
                Source = "register",
            },
        ],
    };

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private async Task<JsonNode> StoredDescriptionAsync(string id)
    {
        using var reader = Store.OpenAsyncSession();
        var command = new Raven.Client.Documents.Commands.GetDocumentsCommand(
            Store.Conventions, id, includes: null, metadataOnly: false);
        await reader.Advanced.RequestExecutor.ExecuteAsync(command, reader.Advanced.Context);
        return JsonNode.Parse(command.Result.Results[0]!.ToString()!)!["Description"]!;
    }

    [Fact]
    public async Task A_TranslatedString_persists_as_Entries_and_equals_the_API_shape()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Car { Id = "cars/1", LicensePlate = "1-AAA-111", Description = Multilingual() });
            await session.SaveChangesAsync();
        }

        var stored = await StoredDescriptionAsync("cars/1");

        stored.ToJsonString(Relaxed).Should().Be(
            """{"Entries":[{"Tag":"en","Value":"Alpha with spaces"},{"Tag":"nl","Value":"Zebra met spaties"},"""
            + """{"Tag":"zh-Hant","Value":"臺北","Kind":"official","ValidFrom":"2020-01-01","Source":"register"}]}""",
            "optional members are omitted, there is no Key and no Translations");

        var api = JsonSerializer.SerializeToNode(Multilingual())!;
        JsonNode.DeepEquals(stored, api).Should().BeTrue("the store and the API are one shape (X17)");
    }

    [Fact]
    public async Task A_stored_TranslatedString_loads_back_equal()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Car { Id = "cars/2", LicensePlate = "2-BBB-222", Description = Multilingual() });
            await session.SaveChangesAsync();
        }

        using var reader = Store.OpenAsyncSession();
        var car = await reader.LoadAsync<Car>("cars/2");

        car.Description.Should().Be(Multilingual());
    }

    /// <summary>
    /// The behavioural half: the generated map's <c>Entries.Where(Tag == …)</c> path must index to a readable
    /// value, a hyphenated tag included (its field name is sanitized to <c>_</c>).
    /// </summary>
    public class Cars_ByDescription : AbstractIndexCreationTask<Car>
    {
        public Cars_ByDescription()
        {
            // Mapping a second, always-present field matters: RavenDB drops a map entry that produces no
            // terms at all, so an index whose ONLY field is a null translation loses the document entirely.
            // A generated index always maps the entity's other properties too, which is why this mirrors that.
            Map = cars => from car in cars
                          select new
                          {
                              car.LicensePlate,
                              Description_nl = car.Description!.Entries.Where(__entry => __entry.Tag == "nl").Select(__entry => __entry.Value).FirstOrDefault(),
                              Description_zh_Hant = car.Description!.Entries.Where(__entry => __entry.Tag == "zh-Hant").Select(__entry => __entry.Value).FirstOrDefault(),
                          };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class VCar
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public string? Description_nl { get; set; }
        public string? Description_zh_Hant { get; set; }
    }

    [Fact]
    public async Task The_entries_path_maps_to_a_readable_index_field()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Car { LicensePlate = "1-AAA-111", Description = Multilingual() });
            await session.StoreAsync(new Car { LicensePlate = "2-BBB-222", Description = null });
            await session.SaveChangesAsync();
        }

        await new Cars_ByDescription().ExecuteAsync(Store);
        await RavenIndexHelper.WaitForNonStaleAsync(Store);

        using var reader = Store.OpenAsyncSession();
        var results = await reader.Query<VCar, Cars_ByDescription>().ProjectInto<VCar>().ToListAsync();

        // A null property indexes to null rather than faulting the index or dropping the document.
        results.Should().HaveCount(2);
        results.Should().ContainSingle(v => v.Description_nl == "Zebra met spaties" && v.Description_zh_Hant == "臺北");
        results.Should().ContainSingle(v => v.Description_nl == null && v.Description_zh_Hant == null);
    }
}
