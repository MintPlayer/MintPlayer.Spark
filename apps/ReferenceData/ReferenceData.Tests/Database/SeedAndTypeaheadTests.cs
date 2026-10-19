using Microsoft.Extensions.Options;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Api;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Search;
using MintPlayer.Spark.ReferenceData.Seed;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using ReferenceData.Tests.Engine;

namespace ReferenceData.Tests.Database;

/// <summary>
/// The basic seed set and the typeahead on the embedded server (PRD §14 round 3, RD9, RD10; RS2, RS6): a fresh database
/// is seeded, a second pass writes nothing, the two-step typeahead finds exact, prefix and misspelled names, streets are
/// narrowed to a municipality, and consumers look places up by code.
/// </summary>
public class SeedAndTypeaheadTests : SparkTestDriver
{
    private static string SeedDirectory => Path.Combine(EngineTests.RepoRoot(), "apps", "ReferenceData", "ReferenceData", "Seed");

    private RefApiService Api()
    {
        var options = Options.Create(new SparkReferenceDataOptions());
        return new RefApiService(Store, new RefTypeahead(options), new RefDataCurrentRelease(Store, TimeProvider.System), RefFixtures.Sources(), TimeProvider.System);
    }

    private async Task SeedAsync()
    {
        // The application's generated index classes: the library declares them (X19), the app emits them.
        await DeployIndexesAsync(typeof(global::ReferenceData.ReferenceDataContext).Assembly);
        var result = await RefDataSeeder.SeedAsync(Store, new DirectoryReleaseFileStore(SeedDirectory), CancellationToken.None);
        result.Added.Should().BeGreaterThan(0);
        await WaitForIndexesAsync();
    }

    [Fact]
    public async Task A_second_seed_writes_nothing()
    {
        await SeedAsync();
        var again = await RefDataSeeder.SeedAsync(Store, new DirectoryReleaseFileStore(SeedDirectory), CancellationToken.None);
        again.Written.Should().Be(0);
    }

    [Fact]
    public async Task The_seed_never_overwrites_what_a_full_import_wrote()
    {
        await SeedAsync();
        using (var session = Store.OpenAsyncSession())
        {
            var place = await session.LoadAsync<RefPlace>("places/be/nis/45068");
            place.TimeZoneNote = "imported";
            place.Hash = "sha256:imported";
            place.Release = new ReleaseStamp { Introduced = RefDataSeed.Release, Changed = "2026.10.0" };
            await session.SaveChangesAsync();
        }
        var again = await RefDataSeeder.SeedAsync(Store, new DirectoryReleaseFileStore(SeedDirectory), CancellationToken.None);
        again.Written.Should().Be(0);
        using var check = Store.OpenAsyncSession();
        (await check.LoadAsync<RefPlace>("places/be/nis/45068")).TimeZoneNote.Should().Be("imported");
    }

    [Fact]
    public async Task A_postal_locality_is_found_by_its_prefix()
    {
        await SeedAsync();
        var response = await Api().SuggestAsync(new SuggestQuery("kruish", "be", [], null, RefFixtures.D("2026-10-19"), "nl", 10), CancellationToken.None);
        response.Items.Should().NotBeEmpty();
        var first = response.Items[0];
        first.Type.Should().Be("postal-locality");
        first.Label.Should().Be("9770 Kruishoutem");
        first.Municipality!.Id.Should().Be("places/be/nis/45068");
        first.TimeZone.Should().Be("Europe/Brussels");
        response.Attribution.Should().Contain(a => a.StartsWith("Statbel"));
    }

    [Fact]
    public async Task Digits_query_postal_codes()
    {
        await SeedAsync();
        var response = await Api().SuggestAsync(new SuggestQuery("97", "be", [], null, RefFixtures.D("2026-10-19"), "nl", 20), CancellationToken.None);
        response.Items.Should().NotBeEmpty();
        response.Items.Should().OnlyContain(i => i.PostalCode != null && i.PostalCode.Code.StartsWith("97"));
    }

    [Fact]
    public async Task A_misspelled_place_is_found_through_the_fuzzy_step()
    {
        // RD10: fuzzy by default, after the prefix tiers ("Oudenarde" for Oudenaarde).
        await SeedAsync();
        var response = await Api().SuggestAsync(new SuggestQuery("Oudenarde", "be", [], null, RefFixtures.D("2026-10-19"), "nl", 10), CancellationToken.None);
        response.Items.Take(3).Should().Contain(i => i.Label.Contains("Oudenaarde"));
        response.Items.Should().Contain(i => i.Matched.Fuzzy);
    }

    [Fact]
    public async Task An_as_of_query_before_the_merger_finds_the_old_municipality()
    {
        await SeedAsync();
        var before = await Api().SuggestAsync(new SuggestQuery("kruishoutem", "be", ["be:municipality"], null, RefFixtures.D("2018-06-01"), "nl", 10), CancellationToken.None);
        before.Items.Should().Contain(i => i.Place!.Id == "places/be/nis/45017");
        var after = await Api().SuggestAsync(new SuggestQuery("kruishoutem", "be", ["be:municipality"], null, RefFixtures.D("2019-06-01"), "nl", 10), CancellationToken.None);
        after.Items.Should().NotContain(i => i.Place != null && i.Place.Id == "places/be/nis/45017");
    }

    [Fact]
    public async Task Streets_are_narrowed_to_a_municipality()
    {
        await SeedAsync();
        using var session = Store.OpenAsyncSession();
        var street = (await Raven.Client.Documents.LinqExtensions.ToListAsync(session.Query<RefStreet>().Where(s => s.Country == "be").Take(512)))
            .First(s => s.Periods.Any(p => p.Municipality == "places/be/nis/45068"));
        var name = street.Names.Entries[0].Value;
        var response = await Api().StreetsAsync(new StreetQuery(name[..Math.Min(4, name.Length)], "be", "places/be/nis/45068", null, RefFixtures.D("2026-10-19"), "nl", 20), CancellationToken.None);
        response.Items.Should().Contain(i => i.Id == street.Id);
        response.Items.Should().OnlyContain(i => i.Municipality.Id == "places/be/nis/45068");
    }

    [Fact]
    public async Task Consumers_look_places_up_by_code_never_by_computing_the_id()
    {
        await SeedAsync();
        var response = await Api().ByCodeAsync("be", "nis", "58001", RefFixtures.D("2026-10-19"), "fr", CancellationToken.None);
        response.Items.Should().ContainSingle(i => i.Id == "places/be/nis/55022");
    }

    [Fact]
    public async Task Time_zone_names_are_the_CLDR_exemplar_cities()
    {
        await SeedAsync();
        (await Api().TimeZonesAsync("nl", CancellationToken.None)).Names["Europe/Brussels"].Should().Be("Brussel");
        (await Api().TimeZonesAsync("fr-BE", CancellationToken.None)).Names["Europe/Brussels"].Should().Be("Bruxelles");
        // English inherits the city from the id: no entry, and the core derives "Brussels" (D36 step 3).
        (await Api().TimeZonesAsync("en", CancellationToken.None)).Names.Should().NotContainKey("Europe/Brussels");
    }

    [Fact]
    public async Task A_query_the_caps_refuse_is_a_400_reason()
    {
        var typeahead = new RefTypeahead(Options.Create(new SparkReferenceDataOptions()));
        new Action(() => typeahead.Fold("a")).Should().Throw<RefQueryException>();
        new Action(() => typeahead.Fold("one two three four five six")).Should().Throw<RefQueryException>();
        new Action(() => typeahead.Fold("kruis\u0000")).Should().Throw<RefQueryException>();
        await Task.CompletedTask;
    }
}
