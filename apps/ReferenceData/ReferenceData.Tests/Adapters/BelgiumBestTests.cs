using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Sources;
using MintPlayer.Spark.ReferenceData.Sources.Belgium;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Best;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Readers;

namespace ReferenceData.Tests.Adapters;

/// <summary>
/// The BeST full bundle (<c>be-best</c>, PRD §4.4, plan R6): the central directory read from the tail (ZIP and ZIP64),
/// the non-address entries fetched by range request and checked against their CRC, the inner files matched by the
/// Lambert-2008-safe pattern, and the XML cut from the real bundle of 2026-10-15/17 mapped onto the Belgian build.
/// </summary>
public class BelgiumBestTests
{
    // ---- the central directory ----

    [Fact]
    public void The_central_directory_is_read_from_the_tail_with_names_sizes_offsets_and_crcs()
    {
        var municipality = Encoding.UTF8.GetBytes(new string('m', 4000));
        var address = Encoding.UTF8.GetBytes(new string('a', 9000));
        var zip = Zip(("BrusselsMunicipality20261017.zip", municipality), ("BrusselsAddress20261017.zip", address));

        var tail = zip.AsSpan(Math.Max(0, zip.Length - ZipCentralDirectory.TailBytes));
        var location = ZipCentralDirectory.Locate(tail, zip.Length);
        var entries = ZipCentralDirectory.ReadEntries(zip.AsSpan((int)location.Offset, (int)location.Size), location.Count);

        entries.Should().HaveCount(2);
        var first = entries.Single(e => e.Name == "BrusselsMunicipality20261017.zip");
        first.Crc32.Should().Be(ZipCrc32.Compute(municipality));
        first.UncompressedSize.Should().Be(municipality.Length);
        first.Method.Should().Be((ushort)8);
        ZipCentralDirectory.LocalHeaderLength(zip.AsSpan((int)first.LocalHeaderOffset, 30)).Should().Be(30 + first.Name.Length);
    }

    [Fact]
    public void A_zip64_directory_takes_its_location_and_sizes_from_the_zip64_records()
    {
        var data = Encoding.ASCII.GetBytes("hello");
        var zip = Zip64("a.txt", data);

        var location = ZipCentralDirectory.Locate(zip, zip.Length);
        var entry = ZipCentralDirectory.ReadEntries(zip.AsSpan((int)location.Offset, (int)location.Size), location.Count).Single();

        location.Count.Should().Be(1);
        entry.Name.Should().Be("a.txt");
        entry.UncompressedSize.Should().Be(5);
        entry.CompressedSize.Should().Be(5);
        entry.LocalHeaderOffset.Should().Be(0);
        entry.Crc32.Should().Be(ZipCrc32.Compute(data));
    }

    [Fact]
    public void A_truncated_or_foreign_tail_is_refused()
    {
        new Action(() => ZipCentralDirectory.Locate(new byte[100], 100)).Should().Throw<InvalidDataException>();
        var zip = Zip(("x.zip", [1, 2, 3]));
        var location = ZipCentralDirectory.Locate(zip, zip.Length);
        new Action(() => ZipCentralDirectory.ReadEntries(zip.AsSpan((int)location.Offset, (int)location.Size - 1), location.Count))
            .Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("FlandersStreetName20261015.zip", true)]
    [InlineData("FlandersStreetName20261015L72.zip", true)]     // before the Lambert 2008 switch
    [InlineData("WalloniaPartOfMunicipality20261016.zip", true)]
    [InlineData("FlandersAddress20261015.zip", false)]           // never fetched (RD5)
    [InlineData("best-ccby-license.docx", false)]
    public void Only_non_address_inner_files_are_selected_by_pattern(string name, bool selected)
        => BelgiumBestSource.Selected([new ZipDirectoryEntry(name, 8, 1, 1, 1, 0)]).Any().Should().Be(selected);

    [Fact]
    public void A_bundle_whose_address_files_alone_changed_keeps_its_version()
    {
        ZipDirectoryEntry[] week1 = [new("FlandersStreetName20261015.zip", 8, 0xAAAA, 10, 20, 0), new("FlandersAddress20261015.zip", 8, 0x1111, 10, 20, 30)];
        ZipDirectoryEntry[] week2 = [week1[0], week1[1] with { Crc32 = 0x2222 }];
        ZipDirectoryEntry[] week3 = [week1[0] with { Crc32 = 0xBBBB }, week1[1]];

        var v1 = ZipCentralDirectory.Fingerprint(BelgiumBestSource.Selected(week1));
        ZipCentralDirectory.Fingerprint(BelgiumBestSource.Selected(week2)).Should().Be(v1);
        ZipCentralDirectory.Fingerprint(BelgiumBestSource.Selected(week3)).Should().NotBe(v1);
    }

    [Fact]
    public async Task The_fetch_reads_only_the_non_address_entries_by_range_request_and_checks_their_crc()
    {
        var xml = ReadFixture("BrusselsStreetName20261017.cut.xml");
        var inner = Zip(("BrusselsStreetName20261017.xml", xml));
        var bundle = Zip(("BrusselsStreetName20261017.zip", inner), ("BrusselsAddress20261017.zip", new byte[50_000]));
        var handler = new RangeServer(bundle, BelgiumBestSource.Url);
        using var http = new HttpClient(handler);
        var source = new BelgiumBestSource();

        var version = await source.CheckAsync(null, http, CancellationToken.None);
        version.Should().NotBeNull();
        (await source.CheckAsync(new SourceState { Version = version!.Version }, http, CancellationToken.None)).Should().BeNull();

        var work = Directory.CreateTempSubdirectory("best-test-");
        try
        {
            var input = await source.FetchAsync(version, http, work, CancellationToken.None);
            input.Files.Select(f => f.Name).Should().BeEquivalentTo(["BrusselsStreetName20261017.xml"]);
            using var stream = input.Files[0].Open();
            BestXmlReader.Read(stream).Should().NotBeEmpty();

            var directory = ZipCentralDirectory.Locate(bundle, bundle.Length);
            var entries = ZipCentralDirectory.ReadEntries(bundle.AsSpan((int)directory.Offset, (int)directory.Size), directory.Count);
            var addressStart = entries.Single(e => e.Name.Contains("Address")).LocalHeaderOffset;
            handler.Ranges.Should().NotBeEmpty();
            handler.Ranges.Should().NotContain(r => r.From >= addressStart && r.From < directory.Offset);   // address entries are never fetched
            handler.FullDownloads.Should().Be(0);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    // ---- the XML ----

    [Fact]
    public void The_xml_reader_reads_names_statuses_types_and_homonym_additions_from_the_real_cut()
    {
        var flanders = Records("FlandersStreetName20261015.cut.xml");
        var ho = flanders.Single(r => r.ObjectId == "202");
        ho.Names.Should().ContainSingle(n => n.Lang == "nl" && n.Value == "Antverpiastraat");
        ho.HomonymAddition.Should().Be("HO");
        ho.Status.Should().Be("current");
        ho.Municipality.Should().Be("11002");
        BestNamespace.StreetRegion(ho.Namespace).Should().Be("vlg");

        Records("WalloniaStreetName20261016.cut.xml").Should().Contain(r => r.Type == "hamlet");

        // Brussels files start with a BOM and name every street in French and Dutch.
        ReadFixture("BrusselsStreetName20261017.cut.xml").AsSpan(0, 3).ToArray().Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF });
        var brussels = Records("BrusselsStreetName20261017.cut.xml");
        brussels.Should().OnlyContain(r => r.Names.Any(n => n.Lang == "fr") && r.Names.Any(n => n.Lang == "nl"));
        BestNamespace.StreetRegion(brussels[0].Namespace).Should().Be("bru");
    }

    [Theory]
    [InlineData("https://data.vlaanderen.be/id/straatnaam/", "vlg")]
    [InlineData("http://www.data.vlaanderen.be/id/straatnaam", "vlg")]
    [InlineData("HTTPS://DATA.VLAANDEREN.BE/ID/STRAATNAAM", "vlg")]
    [InlineData("https://databrussels.be/id/streetname", "bru")]
    [InlineData("geodata.wallonie.be/id/Streetname", "wal")]
    [InlineData("https://data.vlaanderen.be/id/gemeente/", null)]
    public void Namespaces_are_normalized_before_they_name_a_region(string ns, string? region)
        => BestNamespace.StreetRegion(ns).Should().Be(region);

    // ---- the mapping ----

    [Fact]
    public async Task The_bundle_adds_homonym_qualifiers_hamlets_and_unlinked_current_streets_without_breaking_the_seed()
    {
        var withBest = await BuildAsync(withBest: true);
        var without = await BuildAsync(withBest: false);

        withBest.Ledger.Conflicts.Should().BeEmpty();
        // Antverpiastraat (HO) and (EK): current streets no postal code links, added with their qualifier and municipality.
        var ho = withBest.Streets[RefIds.Street("be", "vlg", "202")];
        ho.Qualifier.Should().Be("HO");
        ho.Periods.Should().ContainSingle(p => p.Municipality == "places/be/nis/11002" && p.PostalCodes.Count == 0);
        ho.Source.Should().Contain(BelgiumBestSource.SourceId);
        withBest.Streets[RefIds.Street("be", "vlg", "203")].Qualifier.Should().Be("EK");

        withBest.Streets.Values.Should().Contain(s => s.Type == StreetType.Hamlet);
        // Every street the postal-streets file made is still there, with the same names and postal codes.
        foreach (var (id, street) in without.Streets)
        {
            withBest.Streets[id].Names.Entries.Select(e => e.Value).Should().Equal(street.Names.Entries.Select(e => e.Value));
            withBest.Streets[id].Periods.SelectMany(p => p.PostalCodes).Should().BeEquivalentTo(street.Periods.SelectMany(p => p.PostalCodes));
        }
        withBest.Sources[BelgiumBestSource.SourceId].Attribution.Should().Contain("FOD BOSA BeST Address");
    }

    [Fact]
    public async Task The_mapping_is_deterministic()
    {
        var a = await BuildAsync(withBest: true);
        var b = await BuildAsync(withBest: true);
        a.Documents().Select(d => ReleaseHash(d)).Should().Equal(b.Documents().Select(d => ReleaseHash(d)));
    }

    // ---- helpers ----

    private static string ReleaseHash(object document) => MintPlayer.Spark.ReferenceData.Releases.ReleaseCanonical.Hash(document);

    internal static Task<RefDataBuild> BuildAsync(bool withBest)
    {
        IReferenceDataSource[] sources = withBest
            ? [new BelgiumRefnisSource(), new BelgiumNis6Source(), new BelgiumPostalStreetsSource(), new BelgiumBestSource()]
            : [new BelgiumRefnisSource(), new BelgiumNis6Source(), new BelgiumPostalStreetsSource()];
        return new RefDatasetBuilder(sources, [new BelgiumMapper()]).BuildFixturesAsync(CancellationToken.None);
    }

    private static List<BestRecord> Records(string fixture)
    {
        using var stream = new MemoryStream(ReadFixture(fixture));
        return [.. BestXmlReader.Read(stream)];
    }

    internal static byte[] ReadFixture(string name)
    {
        var file = EmbeddedSourceFiles.Under(typeof(BelgiumBestSource).Assembly, "Fixtures/").Single(f => f.Name == name);
        using var stream = file.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                stream.Write(content);
            }
        return memory.ToArray();
    }

    /// <summary>A one-entry stored ZIP whose central directory uses ZIP64 for the sizes, the offset and the EOCD.</summary>
    private static byte[] Zip64(string name, byte[] data)
    {
        var crc = ZipCrc32.Compute(data);
        var nameBytes = Encoding.ASCII.GetBytes(name);
        using var memory = new MemoryStream();
        using var w = new BinaryWriter(memory);
        // local header
        w.Write(0x04034b50u); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length); w.Write((ushort)nameBytes.Length); w.Write((ushort)0);
        w.Write(nameBytes); w.Write(data);
        // central directory entry with a ZIP64 extra
        var cdOffset = memory.Position;
        w.Write(0x02014b50u); w.Write((ushort)45); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(crc); w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)nameBytes.Length); w.Write((ushort)28); w.Write((ushort)0);
        w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write(0xFFFFFFFFu);
        w.Write(nameBytes);
        w.Write((ushort)0x0001); w.Write((ushort)24); w.Write((ulong)data.Length); w.Write((ulong)data.Length); w.Write(0UL);
        var cdSize = memory.Position - cdOffset;
        // ZIP64 end-of-central-directory record
        var recordOffset = memory.Position;
        w.Write(0x06064b50u); w.Write(44UL); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
        w.Write(1UL); w.Write(1UL); w.Write((ulong)cdSize); w.Write((ulong)cdOffset);
        // ZIP64 locator
        w.Write(0x07064b50u); w.Write(0u); w.Write((ulong)recordOffset); w.Write(1u);
        // end of central directory, saturated
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF);
        w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)0);
        w.Flush();
        return memory.ToArray();
    }

    /// <summary>Serves one file with ETag, HEAD and byte ranges, and records every range asked for.</summary>
    private sealed class RangeServer(byte[] file, string url) : HttpMessageHandler
    {
        public List<(long From, long To)> Ranges { get; } = [];
        public int FullDownloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.ToString() != url)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Headers.ETag = new EntityTagHeaderValue("\"bundle-1\"");
                return Task.FromResult(head);
            }
            if (request.Headers.Range?.Ranges.SingleOrDefault() is not { } range)
            {
                FullDownloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
            }
            long from, to;
            if (range.From is null) { from = Math.Max(0, file.Length - range.To!.Value); to = file.Length - 1; }
            else { from = range.From.Value; to = Math.Min(range.To ?? file.Length - 1, file.Length - 1); }
            Ranges.Add((from, to));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(file[(int)from..(int)(to + 1)]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
            return Task.FromResult(response);
        }
    }
}
