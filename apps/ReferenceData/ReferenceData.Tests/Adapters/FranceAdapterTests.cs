using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.France;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The French adapter (PRD §4.4, plan R8) on its real fixtures: INSEE COG 2026 (Arboys en Bugey reusing code 01015,
/// Saint-Bois, Bohas-Meyriat-Rignat with its communes associées, Bourg → Bourg-en-Bresse, Ajaccio 20004 → 2A004), BAN
/// rows of départements 01 and 2A, and La Poste's base officielle des codes postaux.
/// </summary>
public class FranceAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder([new FranceCogSource(), new FranceBanSource(), new FranceLaPosteSource()], [new FranceMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static Func<string, Task<RefPlace?>> Loader(RefDataBuild build) => id => Task.FromResult(build.Places.GetValueOrDefault(id));

    [Fact]
    public async Task The_profile_carries_the_French_VAT_territories_and_no_single_zone()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var profile = build.Countries["countries/fr"].Profile;
        profile.TimeZone.Should().BeNull();
        profile.HistoryKnownSince.Should().Be(D("1943-01-01"));
        Regex.IsMatch("01300", profile.PostcodePattern!).Should().BeTrue();
        foreach (var code in new[] { "FR-GP", "FR-RE", "FR-BL" }) profile.VatTerritories.Select(t => t.Code).Should().Contain(code);
        Regex.IsMatch("97150", profile.VatTerritories.Single(t => t.Code == "FR-GP").PostcodePattern!).Should().BeFalse();
        build.Places["places/fr/iso3166/FR"].TimeZoneNote.Should().NotBeNull();
    }

    [Fact]
    public async Task Regions_departements_and_communes_form_the_hierarchy()
    {
        var build = await BuildAsync();
        var bourg = build.Places["places/fr/insee/01053"];
        RefAsOf.SegmentAt(bourg, D("2026-01-01"))!.Ancestors
            .Should().Equal("places/fr/insee-dep/01", "places/fr/insee-reg/84", "places/fr/iso3166/FR");
        bourg.TimeZone.Should().Be("Europe/Paris");
        build.Places["places/fr/insee-dep/2A"].Names.Resolve(["fr"]).Should().Be("Corse-du-Sud");
    }

    [Fact]
    public async Task A_reused_INSEE_code_gives_the_commune_nouvelle_a_dated_id()
    {
        var build = await BuildAsync();
        // The plain id stays the commune déléguée Arbignieu (PRD §4.3); the commune nouvelle gets @2016-01-01.
        var arboys = build.Places["places/fr/insee/01015@2016-01-01"];
        arboys.Names.Resolve(["fr"]).Should().Be("Arboys en Bugey");
        arboys.Lifetime.From.Should().Be(D("2016-01-01"));
        build.Ledger.Find("insee:01015").Should().Be("places/fr/insee/01015");

        var arbignieu = build.Places["places/fr/insee/01015"];
        RefAsOf.PeriodAt(arbignieu, D("2015-06-01"))!.Kind.Should().Be("fr:commune");
        var deleguee = RefAsOf.PeriodAt(arbignieu, D("2016-01-01"))!;
        deleguee.Kind.Should().Be("fr:commune-deleguee");
        deleguee.Parent.Should().Be("places/fr/insee/01015@2016-01-01");
        RefAsOf.PeriodAt(build.Places["places/fr/insee/01340"], D("2026-01-01"))!.Parent.Should().Be("places/fr/insee/01015@2016-01-01");

        var merge = build.Lineage["lineage/fr/2016-01-01/merge/insee-01015"];
        merge.Operations.Should().Contain(o => o.Place == "places/fr/insee/01015@2016-01-01" && o.Op == LineageOp.Create);
        merge.Operations.Where(o => o.Op == LineageOp.Demote).Select(o => o.Place).Should().Equal("places/fr/insee/01015", "places/fr/insee/01340");

        // The déléguée continues: as of today it resolves to itself, one level down.
        var now = await RefAsOf.ResolveAsync(arbignieu, D("2026-01-01"), Loader(build));
        now.Places.Single().Id.Should().Be("places/fr/insee/01015");
    }

    [Fact]
    public async Task A_departement_change_is_a_recode_that_keeps_the_id()
    {
        var build = await BuildAsync();
        build.Ledger.Find("insee:2A004").Should().Be("places/fr/insee/20004");
        var ajaccio = build.Places["places/fr/insee/20004"];
        ajaccio.Codes.Should().Contain(c => c.Scheme == "insee" && c.Value == "20004" && c.To == D("1976-01-01"));
        ajaccio.Codes.Should().Contain(c => c.Scheme == "insee" && c.Value == "2A004" && c.From == D("1976-01-01") && c.To == null);
        RefAsOf.PeriodAt(ajaccio, D("2026-01-01"))!.Parent.Should().Be("places/fr/insee-dep/2A");
        // Corse (20) is not in the 2026 edition: no parent before 1976, and a warning, never an invented one.
        RefAsOf.PeriodAt(ajaccio, D("1970-01-01"))!.Parent.Should().BeNull();
        build.Warnings.Should().Contain(w => w.Contains("20004"));
        build.Lineage.Should().ContainKey("lineage/fr/1976-01-01/recode/insee-2A004");
    }

    [Fact]
    public async Task A_rename_is_a_dated_name_on_one_period()
    {
        var build = await BuildAsync();
        var bourg = build.Places["places/fr/insee/01053"];
        bourg.Periods.Should().ContainSingle();
        bourg.Names.Entries.Should().Contain(e => e.Value == "Bourg" && e.ValidTo == D("1955-03-31"));
        bourg.Names.Entries.Should().Contain(e => e.Value == "Bourg-en-Bresse" && e.ValidFrom == D("1955-03-31"));
        build.Lineage.Should().ContainKey("lineage/fr/1955-03-31/rename/insee-01053");
    }

    [Fact]
    public async Task Communes_associees_continue_one_level_down_and_end_into_their_commune()
    {
        var build = await BuildAsync();
        var rignat = build.Places["places/fr/insee/01324"];
        var associee = RefAsOf.PeriodAt(rignat, D("1990-01-01"))!;
        associee.Kind.Should().Be("fr:commune-associee");
        associee.Parent.Should().Be("places/fr/insee/01245");
        rignat.Lifetime.To.Should().Be(D("2026-01-01"));
        rignat.SupersededBy.Should().ContainSingle(e => e.Id == "places/fr/insee/01245" && e.Date == D("2026-01-01"));
        build.Places["places/fr/insee/01048"].Lifetime.To.Should().Be(D("2000-01-01"));
    }

    [Fact]
    public async Task BAN_streets_lie_in_the_current_commune_and_their_commune_deleguee()
    {
        var build = await BuildAsync();
        var fontaine = build.Streets["streets/fr/ban-voie/01015_0712"];
        fontaine.Names.Resolve(["fr"]).Should().Be("Impasse de l’Ancienne Fontaine");
        fontaine.Periods.Should().ContainSingle();
        fontaine.Periods[0].Municipality.Should().Be("places/fr/insee/01015@2016-01-01");
        fontaine.Periods[0].Locality.Should().Be("places/fr/insee/01340");
        fontaine.Periods[0].PostalCodes.Should().Equal("postalcodes/fr/01300");

        // A row without id_fantoir: the toponym key comes from the address id.
        build.Streets["streets/fr/ban-voie/01015_DZCI17"].Periods[0].Locality.Should().Be("places/fr/insee/01015");
        // Corsica: 2A is not numeric.
        build.Streets["streets/fr/ban-voie/2A004_0970"].Periods[0].PostalCodes.Should().Equal("postalcodes/fr/20000");
        // Meyriat is Bohas-Meyriat-Rignat's own former code: no separate locality.
        build.Streets["streets/fr/ban-voie/01245_0124"].Periods[0].Locality.Should().BeNull();
    }

    [Fact]
    public async Task Postal_localities_come_from_BAN_and_La_Poste_line_5()
    {
        var build = await BuildAsync();
        var postal = build.PostalCodes["postalcodes/fr/01300"];
        postal.Localities.Should().Contain(l => l.Key == "arboysenbugey" && l.Place == "places/fr/insee/01015@2016-01-01" && l.Main);
        postal.Localities.Should().Contain(l => l.Key == "arbignieu" && l.Place == "places/fr/insee/01015");
        // "ST BOIS" is matched to Saint-Bois.
        postal.Localities.Should().Contain(l => l.Key == "saintbois" && l.Place == "places/fr/insee/01340");
        // A lieu-dit without a place of its own.
        build.PostalCodes["postalcodes/fr/20167"].Localities.Should().Contain(l => l.Key == "mezzavia" && l.Place == null);
        foreach (var source in new[] { "fr-ban", "fr-laposte" }) postal.Source.Should().Contain(source);
    }

    [Fact]
    public async Task Attribution_follows_INSEE_and_Etalab()
    {
        var build = await BuildAsync();
        build.Sources["fr-cog"].Attribution.Should().StartWith("Source : Insee");
        build.Sources["fr-ban"].Licence.Should().Be("Licence Ouverte 2.0");
        build.Sources["fr-laposte"].Attribution.Should().Contain("La Poste");
    }

    [Fact]
    public async Task Two_builds_are_identical()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(ReleaseCanonical.Hash).Should().Equal(second.Documents().Select(ReleaseCanonical.Hash));
    }
}
