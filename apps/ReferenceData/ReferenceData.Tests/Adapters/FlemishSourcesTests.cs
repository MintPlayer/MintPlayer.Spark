using System.Net;
using System.Text;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Sources;
using MintPlayer.Spark.ReferenceData.Sources.Belgium;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Flemish;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The Flemish sources (plan R6): the 2025 merger diff files (Q-R2: a 1 → 1 street mapping continues the street;
/// merges and splits end streets with lineage) and the Basisregisters API (official and facility languages, retired
/// streets). Fixtures: rows of the real diff files cut to the seeded municipalities, and real API responses recorded
/// on 2026-10-19.
/// </summary>
public class FlemishSourcesTests
{
    private static readonly DateOnly Merger = new(2025, 1, 1);

    [Fact]
    public void The_diff_files_parse_with_their_bom_and_register_uris()
    {
        using var municipalities = new MemoryStream(BelgiumBestTests.ReadFixture("fusies-verschilbestand-gemeenten-20241213.cut.csv"));
        FlemishDiffReader.Municipalities(municipalities).Should().Contain(m => m.OldNis == "73040" && m.NewNis == "71072" && m.NewName == "Hasselt");

        using var streets = new MemoryStream(BelgiumBestTests.ReadFixture("fusies-verschilbestand-straatnamen-20241218.cut.csv"));
        var rows = FlemishDiffReader.Streets(streets).ToList();
        var renamed = rows.Single(r => r.OldId == "122212");
        renamed.OldName.Should().Be("Molenstraat");
        renamed.NewName.Should().Be("Coenegrachtsmolenstraat");
        renamed.NewId.Should().Be("232891");
        renamed.NewNamespace.Should().Be("https://data.vlaanderen.be/id/straatnaam");
    }

    [Fact]
    public async Task A_one_to_one_mapping_continues_the_street_with_dated_codes_and_periods()
    {
        var build = await BuildAsync();
        build.Ledger.Conflicts.Should().BeEmpty();

        // Hasselt's Kerkstraat: 114824 in Hasselt 71022 until 2025, 232304 in the new Hasselt 71072 since.
        var id = RefIds.Street("be", "vlg", "232304");
        build.Ledger.Find("best-vlg:114824").Should().Be(id);
        build.Streets.Should().NotContainKey(RefIds.Street("be", "vlg", "114824"));
        var street = build.Streets[id];
        street.Codes.Should().Contain(c => c.Value == "114824" && c.To == Merger && c.From == null);
        street.Codes.Should().Contain(c => c.Value == "232304" && c.From == Merger);
        street.Periods.Should().Contain(p => p.Municipality == "places/be/nis/71022" && p.To == Merger && p.PostalCodes.Count > 0);
        street.Periods.Should().Contain(p => p.Municipality == "places/be/nis/71072" && p.From == Merger && p.To == null);
        street.Source.Should().Contain(FlemishDiffSource.SourceId);
    }

    [Fact]
    public async Task A_street_joining_a_continuing_municipality_keeps_its_start()
    {
        var build = await BuildAsync();

        // Borsbeek's August Van Putlei (3681) continues as Antwerpen's existing 244: no new start date for 244.
        var street = build.Streets[RefIds.Street("be", "vlg", "244")];
        street.Codes.Should().Contain(c => c.Value == "244" && c.From == null);
        street.Codes.Should().Contain(c => c.Value == "3681" && c.To == Merger);
        street.Periods.Should().Contain(p => p.Municipality == "places/be/nis/11002" && p.From == null && p.To == null);
        street.Periods.Should().Contain(p => p.Municipality == "places/be/nis/11007" && p.To == Merger);
    }

    [Fact]
    public async Task A_renamed_homonym_keeps_its_old_name_as_historic()
    {
        var build = await BuildAsync();

        var street = build.Streets[RefIds.Street("be", "vlg", "232891")];
        street.Names.Entries.Should().Contain(e => e.Value == "Molenstraat" && e.Kind == TranslatedStringKind.Historic && e.ValidTo == Merger);
        street.Names.Entries.Should().Contain(e => e.Value == "Coenegrachtsmolenstraat" && e.Kind == TranslatedStringKind.Official && e.ValidFrom == Merger);
        street.Search.Tokens.Should().Contain("molenstraat");
    }

    [Fact]
    public async Task Several_old_streets_into_one_is_a_merge_with_ended_documents_and_lineage()
    {
        const string csv =
            "OUD NIS code;OUD straatnaamid;OUD gemeentenaam;OUD postcode RR;OUD straatcode RR;OUD straatnaam;OUD homoniemtoevoeging;NIEUW NIS code;NIEUW straatnaamid;NIEUW gemeentenaam;NIEUW postcode RR;NIEUW straatcode RR;NIEUW straatnaam;NIEUW homoniemtoevoeging\r\n" +
            "71022;https://data.vlaanderen.be/id/straatnaam/900002;Hasselt;;;Kerkstraat;;71072;https://data.vlaanderen.be/id/straatnaam/232304;Hasselt;;;Kerkstraat;\r\n" +
            "73040;https://data.vlaanderen.be/id/straatnaam/900001;Kortessem;;;Kerkweg;;71072;https://data.vlaanderen.be/id/straatnaam/232304;Hasselt;;;Kerkstraat;\r\n";
        // Invented old ids (9000xx), so the test never meets an id the shipped ledger holds.
        var build = await BuildAsync(diff: Input(FlemishDiffSource.SourceId, ("fusies-verschilbestand-straatnamen-20991231.csv", csv)));

        var oldHasselt = build.Streets[RefIds.Street("be", "vlg", "900002")];
        oldHasselt.Periods.Should().ContainSingle(p => p.Municipality == "places/be/nis/71022" && p.To == Merger);
        oldHasselt.Names.Entries.Should().ContainSingle(e => e.Value == "Kerkstraat" && e.ValidTo == Merger);
        build.Streets[RefIds.Street("be", "vlg", "900001")].Periods.Should().ContainSingle(p => p.Municipality == "places/be/nis/73040");

        var lineage = build.Lineage[RefIds.LineageEvent("be", Merger, LineageKind.Merge, "best-vlg", "232304")];
        lineage.Operations.Should().Contain(o => o.Place == RefIds.Street("be", "vlg", "900002") && o.Op == LineageOp.End && o.Into == RefIds.Street("be", "vlg", "232304"));
        lineage.Operations.Should().Contain(o => o.Place == RefIds.Street("be", "vlg", "900001") && o.Op == LineageOp.End);
        lineage.Operations.Should().Contain(o => o.Place == RefIds.Street("be", "vlg", "232304") && o.Op == LineageOp.Create);
    }

    [Fact]
    public async Task One_old_street_into_several_is_a_split_with_part_shares()
    {
        const string csv =
            "OUD NIS code;OUD straatnaamid;OUD gemeentenaam;OUD postcode RR;OUD straatcode RR;OUD straatnaam;OUD homoniemtoevoeging;NIEUW NIS code;NIEUW straatnaamid;NIEUW gemeentenaam;NIEUW postcode RR;NIEUW straatcode RR;NIEUW straatnaam;NIEUW homoniemtoevoeging\r\n" +
            "71022;https://data.vlaanderen.be/id/straatnaam/900003;Hasselt;;;Kerkstraat;;71072;https://data.vlaanderen.be/id/straatnaam/232304;Hasselt;;;Kerkstraat;\r\n" +
            "71022;https://data.vlaanderen.be/id/straatnaam/900003;Hasselt;;;Kerkstraat;;71072;https://data.vlaanderen.be/id/straatnaam/232303;Hasselt;;;Kerkhofstraat;\r\n";
        var build = await BuildAsync(diff: Input(FlemishDiffSource.SourceId, ("fusies-verschilbestand-straatnamen-20991231.csv", csv)));

        var lineage = build.Lineage[RefIds.LineageEvent("be", Merger, LineageKind.Split, "best-vlg", "900003")];
        lineage.Operations.Where(o => o.Op == LineageOp.End).Should().OnlyContain(o => o.Share == SuccessionShare.Part);
        lineage.Operations.Where(o => o.Op == LineageOp.Create).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_download_that_is_not_the_pinned_file_is_refused()
    {
        using var http = new HttpClient(new FixedResponse(Encoding.UTF8.GetBytes("not the diff zip")));
        var source = new FlemishDiffSource();
        var version = await source.CheckAsync(null, http, CancellationToken.None);
        version!.Sha256.Should().Be(FlemishDiffSource.PinnedSha256);
        (await source.CheckAsync(new SourceState { Version = FlemishDiffSource.Version }, http, CancellationToken.None)).Should().BeNull();

        var work = Directory.CreateTempSubdirectory("flemish-diff-test-");
        try
        {
            await new Func<Task>(() => source.FetchAsync(version, http, work, CancellationToken.None)).Should().ThrowAsync<InvalidDataException>();
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_api_responses_parse_status_languages_and_names()
    {
        var voeren = (FlemishApiMunicipality)FlemishApiReader.Read(new MemoryStream(BelgiumBestTests.ReadFixture("flemish-api-gemeente-73109.json")));
        voeren.OfficialLanguages.Should().Equal(["nl"]);
        voeren.FacilityLanguages.Should().Equal(["fr"]);
        voeren.Retired.Should().BeFalse();

        var hasselt = (FlemishApiMunicipality)FlemishApiReader.Read(new MemoryStream(BelgiumBestTests.ReadFixture("flemish-api-gemeente-71022.json")));
        hasselt.Retired.Should().BeTrue();

        var kerkstraat = (FlemishApiStreet)FlemishApiReader.Read(new MemoryStream(BelgiumBestTests.ReadFixture("flemish-api-straatnaam-114824.json")));
        kerkstraat.Retired.Should().BeTrue();
        kerkstraat.Municipality.Should().Be("71022");
        kerkstraat.Names.Should().ContainSingle(n => n.Value == "Kerkstraat" && n.Lang == "nl");
    }

    [Fact]
    public async Task Facility_languages_are_kept_as_data_and_retired_streets_end_with_their_municipality()
    {
        const string municipality = """
            {"@type":"Gemeente","identificator":{"objectId":"71072"},"officieleTalen":["nl"],"faciliteitenTalen":["fr"],"gemeentenamen":[{"spelling":"Hasselt","taal":"nl"}],"gemeenteStatus":"inGebruik"}
            """;
        // A retired street of Kruishoutem (merged into Kruisem in 2019, before any diff file): doc-shaped, id invented.
        const string street = """
            {"@type":"Straatnaam","identificator":{"objectId":"990001"},"gemeente":{"objectId":"45017"},"straatnamen":[{"spelling":"Oude Testweg","taal":"nl"}],"homoniemToevoegingen":[],"straatnaamStatus":"gehistoreerd"}
            """;
        var build = await BuildAsync(api: Input(FlemishApiSource.SourceId, ("flemish-api-gemeente-71072.json", municipality), ("flemish-api-straatnaam-990001.json", street)));

        build.Places["places/be/nis/71072"].Codes.Should().Contain(c => c.Scheme == "facility-language" && c.Value == "fr");
        build.Places["places/be/nis/71072"].OfficialLanguages.Select(l => l.Lang).Should().Equal(["nl"]);

        var retired = build.Streets[RefIds.Street("be", "vlg", "990001")];
        retired.Periods.Should().ContainSingle(p => p.Municipality == "places/be/nis/45017" && p.To == new DateOnly(2019, 1, 1));
        retired.Names.Entries.Should().ContainSingle(e => e.Value == "Oude Testweg" && e.ValidTo == new DateOnly(2019, 1, 1));
    }

    [Fact]
    public async Task The_recorded_api_fixture_changes_nothing_but_sources_on_the_seed()
    {
        var with = await BuildAsync();
        with.Ledger.Conflicts.Should().BeEmpty();
        with.Sources[FlemishApiSource.SourceId].Attribution.Should().Contain("Bron: Digitaal Vlaanderen");
        with.Sources[FlemishDiffSource.SourceId].Attribution.Should().Contain("Bron: Digitaal Vlaanderen");
        // Voeren is not in the seed: its response is read and skipped, never turned into a place.
        with.Ledger.Find("nis:73109").Should().BeNull();
    }

    // ---- helpers ----

    private static async Task<RefDataBuild> BuildAsync(SourceInput? diff = null, SourceInput? api = null)
    {
        IReferenceDataSource[] sources = [new BelgiumRefnisSource(), new BelgiumNis6Source(), new BelgiumPostalStreetsSource(), new FlemishDiffSource(), new FlemishApiSource()];
        var builder = new RefDatasetBuilder(sources, [new BelgiumMapper()]);
        var inputs = builder.Fixtures().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (diff is not null) inputs[diff.SourceId] = diff;
        if (api is not null) inputs[api.SourceId] = api;
        return await builder.BuildAsync(inputs, null, CancellationToken.None);
    }

    private static SourceInput Input(string sourceId, params (string Name, string Text)[] files) => new()
    {
        SourceId = sourceId,
        Version = "test",
        Files = [.. files.Select(f => new SourceFile(f.Name, () => new MemoryStream(Encoding.UTF8.GetBytes(f.Text))))],
    };

    private sealed class FixedResponse(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
