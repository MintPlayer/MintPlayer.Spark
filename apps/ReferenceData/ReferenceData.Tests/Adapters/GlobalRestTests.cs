using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Sources;
using MintPlayer.Spark.ReferenceData.Sources.Belgium;
using MintPlayer.Spark.ReferenceData.Sources.Global;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The global name sources (plan R7): NGA GNS conventional names (doc-derived fixture: the published country files were
/// empty on 2026-10-19), UN/LOCODE codes and conventional names (a real cut of the 2026-10-16 release) and the IANA
/// Language Subtag Registry (a real cut of File-Date 2026-09-17), run over the Belgian build.
/// </summary>
public class GlobalRestTests
{
    [Fact]
    public void The_subtag_registry_knows_deprecations_and_preferred_values()
    {
        var registry = Registry();
        registry.FileDate.Should().Be("2026-09-17");
        registry.Check("nl").Should().BeEmpty();
        registry.Check("nl-BE").Should().BeEmpty();
        registry.Check("sr-Latn").Should().BeEmpty();
        registry.Check("zh-Hant").Should().BeEmpty();
        registry.Check("x-private").Should().NotBeEmpty();
        registry.Check("iw").Should().ContainSingle(p => p.Contains("deprecated") && p.Contains("'he'"));
        registry.Problem("region", "BU").Should().Contain("deprecated");
        registry.Problem("region", "BE").Should().BeNull();
        registry.Problem("language", "qqq").Should().Contain("not registered");
        IanaSubtags.ReadFileDate("File-Date: 2026-09-17").Should().Be("2026-09-17");
        IanaSubtags.ReadFileDate("Type: language").Should().BeNull();
    }

    [Fact]
    public async Task UN_LOCODE_codes_go_to_the_current_municipality_of_that_name()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();

        // Hasselt: the municipality since 2025 gets the code, the ended one does not (current ones first).
        build.Places["places/be/nis/71072"].Codes.Should().Contain(c => c.Scheme == "unlocode" && c.Value == "BEHSS");
        build.Places["places/be/nis/71022"].Codes.Should().NotContain(c => c.Scheme == "unlocode");
        // "Brussel (Bruxelles)": both parts of the parenthesised name match the City of Brussels.
        build.Places["places/be/nis/21004"].Codes.Should().ContainSingle(c => c.Scheme == "unlocode" && c.Value == "BEBRU");
        // Only municipalities: the province of Antwerp gets no location code.
        build.Places["places/be/nis/11002"].Codes.Should().Contain(c => c.Scheme == "unlocode" && c.Value == "BEANR");
        build.Places["places/be/nis/10000"].Codes.Should().NotContain(c => c.Scheme == "unlocode");
    }

    [Fact]
    public async Task Reference_rows_and_GNS_conventional_names_become_exonyms_once()
    {
        var build = await BuildAsync();

        // UN/LOCODE "Antwerp = Antwerpen": the city's English name.
        build.Places["places/be/nis/11002"].Names.Entries.Should().ContainSingle(e => e.Value == "Antwerp" && e.Tag == "en" && e.Kind == TranslatedStringKind.Exonym && e.Source == UnLocodeSource.SourceId);
        // GNS: the provinces (ADM1) get their conventional English names; the city does not get the province's.
        var east = build.Places["places/be/nis/40000"];
        east.Names.Entries.Should().ContainSingle(e => e.Value == "East Flanders" && e.Tag == "en" && e.Source == GnsSource.SourceId);
        east.Names.Entries.Should().NotContain(e => e.Value == "Flandre-Orientale");   // a variant (V), and the French name already folds to it
        east.Source.Should().Contain(GnsSource.SourceId);
        build.Places["places/be/nis/10000"].Names.Entries.Should().ContainSingle(e => e.Value == "Antwerp" && e.Source == GnsSource.SourceId);
        build.Places["places/be/nis/11002"].Names.Entries.Should().NotContain(e => e.Source == GnsSource.SourceId);
        // Latin-script places: a conventional name is an exonym, not a romanization.
        east.Names.Entries.Where(e => e.Source == GnsSource.SourceId).Should().OnlyContain(e => e.Romanization == null);
        east.Search.Tokens.Should().Contain("flanders");
    }

    [Fact]
    public async Task Every_tag_of_the_belgian_build_is_registered()
    {
        var build = await BuildAsync();
        build.Warnings.Should().NotContain(w => w.StartsWith("global-iana-subtags", StringComparison.Ordinal));
        build.Sources[IanaSubtagSource.SourceId].Licence.Should().Be("CC0 1.0");
        build.Sources[UnLocodeSource.SourceId].Attribution.Should().Contain("UNECE");
        build.Sources[GnsSource.SourceId].Attribution.Should().Contain("National Geospatial-Intelligence Agency");
    }

    [Fact]
    public async Task An_unregistered_or_deprecated_tag_is_listed()
    {
        var builder = Builder();
        var build = await builder.BuildAsync(builder.Fixtures(), null, CancellationToken.None);
        var registry = Registry();
        var place = build.Places.Values.First();
        place.Names.Entries.Add(new TranslatedStringEntry("iw", "x"));
        // The validation is the mapper's last step; run it again over the changed build through a second mapper pass.
        await new GlobalNamesMapper().MapAsync(build, new Dictionary<string, SourceInput> { [IanaSubtagSource.SourceId] = new IanaSubtagSource().Fixture() }, CancellationToken.None);
        build.Warnings.Should().Contain(w => w.Contains("'iw'") && w.Contains("use 'he'"));
        registry.Count.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task The_name_sources_are_deterministic()
    {
        var a = await BuildAsync();
        var b = await BuildAsync();
        a.Documents().Select(ReleaseCanonical.Hash).Should().Equal(b.Documents().Select(ReleaseCanonical.Hash));
    }

    private static RefDatasetBuilder Builder()
        => new([new BelgiumRefnisSource(), new BelgiumNis6Source(), new BelgiumPostalStreetsSource(), new GnsSource(), new UnLocodeSource(), new IanaSubtagSource()],
               [new BelgiumMapper(), new GlobalNamesMapper()]);

    private static Task<RefDataBuild> BuildAsync() => Builder().BuildFixturesAsync(CancellationToken.None);

    private static IanaSubtags Registry()
    {
        var file = new IanaSubtagSource().Fixture().Files.Single();
        using var stream = file.Open();
        return IanaSubtags.Read(stream);
    }
}
