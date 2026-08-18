using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Pins the <em>persisted</em> shape of a <see cref="TranslatedString"/>, which generated per-language index
/// fields depend on.
/// <para>
/// <c>TranslatedStringJsonConverter</c> writes the flat form <c>{"en":..,"nl":..}</c>, but it is a
/// System.Text.Json converter and applies only at the HTTP / <c>PersistentObject</c> boundary. RavenDB persists
/// through Newtonsoft, where nothing is registered for the type, so the stored document is nested as
/// <c>Description.Translations.nl</c> — and that is the path a generated index maps.
/// </para>
/// <para>
/// <strong>Why this test exists.</strong> Registering a Newtonsoft converter to make persistence "consistent
/// with the API shape" is an entirely reasonable-sounding change that would silently empty every generated
/// per-language index field: no deploy failure, no index error, index state healthy, correct row counts, empty
/// values. The model hash would not move and <c>--spark-verify-model</c> would still pass. Nothing else in the
/// suite would notice, so this fails instead.
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
        Translations = new Dictionary<string, string>
        {
            ["en"] = "Alpha with spaces",
            ["nl"] = "Zebra met spaties",
        },
    };

    [Fact]
    public async Task A_TranslatedString_persists_nested_under_Translations()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Car { Id = "cars/1", LicensePlate = "1-AAA-111", Description = Multilingual() });
            await session.SaveChangesAsync();
        }

        using var reader = Store.OpenAsyncSession();

        var command = new Raven.Client.Documents.Commands.GetDocumentsCommand(
            Store.Conventions, "cars/1", includes: null, metadataOnly: false);
        await reader.Advanced.RequestExecutor.ExecuteAsync(command, reader.Advanced.Context);

        var document = command.Result.Results[0]!.ToString()!;

        // The nested form the index maps.
        document.Should().Contain("\"Translations\"");
        document.Should().Contain("\"nl\":\"Zebra met spaties\"");

        // The flat form would put the language key directly under the property.
        document.Should().NotContain("\"Description\":{\"en\"");
    }

    /// <summary>
    /// The behavioural half: the CLR dictionary indexer is what a generated map uses, so it must survive the
    /// round-trip and index to a readable value.
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
                              Description_nl = car.Description!.Translations["nl"],
                          };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class VCar
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public string? Description_nl { get; set; }
    }

    [Fact]
    public async Task The_dictionary_indexer_maps_to_a_readable_index_field()
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
        results.Should().ContainSingle(v => v.Description_nl == "Zebra met spaties");
        results.Should().ContainSingle(v => v.Description_nl == null);
    }
}
