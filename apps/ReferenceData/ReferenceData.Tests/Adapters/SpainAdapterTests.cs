using System.Globalization;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources.Spain;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Spanish adapter (PRD §4.4, plan R8) on its fixtures: real cuts of INE's 2026 municipality list, community table,
/// the Callejero of July 2026 and its January–July 2026 variations; a doc-derived CartoCiudad attribute table.
/// </summary>
public class SpainAdapterTests
{
    private static Task<RefDataBuild> BuildAsync()
        => new RefDatasetBuilder(
                [new SpainIneCodesSource(), new SpainCallejeroSource(), new SpainCallejeroVariationsSource(), new SpainCartoCiudadSource()],
                [new SpainMapper()])
            .BuildFixturesAsync(CancellationToken.None);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static string? Official(RefPlace place, string lang)
        => place.Names.Entries.FirstOrDefault(e => e.Tag == lang && e.Kind == TranslatedStringKind.Official)?.Value;

    [Fact]
    public async Task The_profile_has_three_zones_and_the_special_VAT_territories()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();
        var profile = build.Countries["countries/es"].Profile;
        profile.TimeZone.Should().BeNull();
        Regex.IsMatch("20280", profile.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("53000", profile.PostcodePattern!).Should().BeFalse();
        var canaries = profile.VatTerritories.Single(t => t.Code == "ES-CN");
        canaries.Treatment.Should().Be(VatTerritoryTreatment.OutsideVatInsideCustoms);
        Regex.IsMatch("35001", canaries.PostcodePattern!).Should().BeTrue();
        Regex.IsMatch("38001", canaries.PostcodePattern!).Should().BeTrue();
        profile.VatTerritories.Single(t => t.Code == "ES-CE").Treatment.Should().Be(VatTerritoryTreatment.OutsideVatAndCustoms);
        profile.VatTerritories.Should().OnlyContain(t => t.LegalBasis.StartsWith("Directive 2006/112/EC") && t.Note != null);

        build.Places["places/es/ine/35016"].TimeZone.Should().Be("Atlantic/Canary");
        build.Places["places/es/ine/51001"].TimeZone.Should().Be("Africa/Ceuta");
        build.Places["places/es/ine/20069"].TimeZone.Should().Be("Europe/Madrid");
    }

    [Fact]
    public async Task Bilingual_names_are_split_and_inverted_articles_restored()
    {
        var build = await BuildAsync();
        var donostia = build.Places["places/es/ine/20069"];
        Official(donostia, "eu").Should().Be("Donostia");
        Official(donostia, "es").Should().Be("San Sebastián");
        donostia.OfficialLanguages.Select(l => l.Lang).Should().Equal("eu", "es");

        Official(build.Places["places/es/ine/15030"], "gl").Should().Be("A Coruña");
        Official(build.Places["places/es/ine/15030"], "es").Should().Be("A Coruña");
        Official(build.Places["places/es/ine/35016"], "es").Should().Be("Las Palmas de Gran Canaria");
        Official(build.Places["places/es/ine-prov/35"], "es").Should().Be("Las Palmas");
        Official(build.Places["places/es/ine-ccaa/16"], "es").Should().Be("País Vasco");
        Official(build.Places["places/es/ine-ccaa/15"], "es").Should().Be("Comunidad Foral de Navarra");
    }

    [Theory]
    [InlineData("Coruña, A", "A Coruña")]
    [InlineData("Palmas de Gran Canaria, Las", "Las Palmas de Gran Canaria")]
    [InlineData("Hospitalet de Llobregat, L'", "L'Hospitalet de Llobregat")]
    [InlineData("Asturias, Principado de", "Principado de Asturias")]
    [InlineData("Valle de Egüés/Eguesibar", "Valle de Egüés/Eguesibar")]
    public void Inverted_names_are_restored(string ine, string expected) => SpainNames.Uninvert(ine).Should().Be(expected);

    [Theory]
    [InlineData("ZARDO (EL)", "El Zardo")]
    [InlineData("ELVIÑA (SAN VICENTE DE)", "San Vicente de Elviña")]
    [InlineData("AGITI DE", "de Agiti")]
    [InlineData("ABREU Y GALINDO", "Abreu y Galindo")]
    public void Upper_case_callejero_names_are_title_cased(string upper, string expected) => SpainNames.TitleCase(upper).Should().Be(expected);

    [Fact]
    public async Task The_hierarchy_runs_community_province_municipality_entity()
    {
        var build = await BuildAsync();
        var ancestors = RefAsOf.SegmentAt(build.Places["places/es/ine-ue/2006900017"], D("2026-10-19"))!.Ancestors;
        ancestors.Should().Equal("places/es/ine/20069", "places/es/ine-prov/20", "places/es/ine-ccaa/16", "places/es/iso3166/ES");
        Official(build.Places["places/es/ine-ue/2006900017"], "es").Should().Be("Añorga");
        Official(build.Places["places/es/ine-ue/3501600120"], "es").Should().Be("Los Giles");
        // Nuclei and the "*DISEMINADO*" rows are not entities.
        build.Places.Keys.Should().NotContain(k => k.StartsWith("places/es/ine-ue/") && k.EndsWith("99"));
    }

    [Fact]
    public async Task Streets_are_named_with_their_type_unless_the_name_holds_it()
    {
        var build = await BuildAsync();
        build.Streets["streets/es/ine/35016-00004"].Names.Entries[0].Value.Should().Be("Calle Abreu y Galindo");
        build.Streets["streets/es/ine/51001-00007"].Names.Entries[0].Value.Should().Be("Avenida de Africa");
        build.Streets["streets/es/ine/20069-00040"].Names.Entries[0].Value.Should().Be("Camino de Agiti");
        // Basque type words are part of the name already.
        build.Streets["streets/es/ine/20036-01101"].Names.Entries[0].Value.Should().Be("Kale Nagusia");
        build.Streets["streets/es/ine/20036-01104"].Names.Entries[0].Value.Should().Be("Fraxkuneko Murrua Kalea");
        build.Streets["streets/es/ine/20036-01101"].Periods.Single().Municipality.Should().Be("places/es/ine/20036");
    }

    [Fact]
    public async Task Variation_files_date_new_streets_and_report_unknown_bajas()
    {
        var build = await BuildAsync();
        var alta = build.Streets["streets/es/ine/15030-01854"];
        alta.Names.Entries[0].Value.Should().Be("Calle Marquesa de Cavalcanti");
        alta.Periods.Single().From.Should().Be(D("2026-03-13"));
        alta.Source.Should().Contain(SpainCallejeroVariationsSource.SourceId);
        build.Warnings.Should().Contain(w => w.Contains("35016-01379"));
    }

    [Fact]
    public async Task Postcodes_come_from_CartoCiudad_with_the_IGN_formula_in_every_attribution()
    {
        var build = await BuildAsync();
        var hondarribia = build.PostalCodes["postalcodes/es/20280"];
        hondarribia.Localities.Should().ContainSingle(l => l.Place == "places/es/ine/20036" && l.Municipalities.Any(m => m.Id == "places/es/ine/20036"));
        hondarribia.Source.Should().Equal(SpainCartoCiudadSource.SourceId);
        new SpainCartoCiudadSource().Licence.Compact.Should().Contain("Obra derivada de CartoCiudad CC-BY 4.0 scne.es");
        build.Sources["es-ine"].Attribution.Should().Be("Elaboración propia con datos extraídos del sitio web del INE: www.ine.es");
    }

    [Fact]
    public async Task CartoCiudad_has_no_automatic_change_check()
    {
        using var http = new HttpClient();
        (await new SpainCartoCiudadSource().CheckAsync(null, http, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Two_builds_give_the_same_bytes()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        first.Documents().Select(d => ReleaseCanonical.Hash(d)).Should().Equal(second.Documents().Select(d => ReleaseCanonical.Hash(d)));
    }
}
