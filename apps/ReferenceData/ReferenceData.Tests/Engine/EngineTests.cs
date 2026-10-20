using System.Text;
using MintPlayer.Spark.ReferenceData;
using MintPlayer.Spark.ReferenceData.Build;
using MintPlayer.Spark.ReferenceData.Releases;
using MintPlayer.Spark.ReferenceData.Seed;
using MintPlayer.Spark.ReferenceData.Sources;
using static ReferenceData.Tests.Engine.RefFixtures;

namespace ReferenceData.Tests.Engine;

/// <summary>Ids, the ledger, search keys, determinism and the release format (PRD §4.3, §4.6, §4.10; RS3, RS7, RS10).</summary>
public class EngineTests
{
    [Theory]
    [InlineData("places/be/nis/45068", true)]
    [InlineData("places/be/nis6/45017Z", true)]
    [InlineData("places/fr/insee/01015@2016-01-01", true)]
    [InlineData("places/be/nis/45068/", false)]
    [InlineData("places/be/nis/45068|", false)]
    [InlineData("places/be//45068", false)]
    [InlineData("people/be/nis/45068", false)]
    [InlineData("places/be/nis/' or 1=1", false)]
    public void The_id_grammar_refuses_malformed_ids(string id, bool valid)
        => RefIds.IsValid(id).Should().Be(valid);

    [Fact]
    public void Id_builders_validate_every_segment()
    {
        RefIds.Place("BE", "NIS", "45068").Should().Be("places/be/nis/45068");
        RefIds.Street("be", "vlg", "73243").Should().Be("streets/be/vlg/73243");
        new Action(() => RefIds.Place("be", "nis", "45/068")).Should().Throw<ArgumentException>();
        new Action(() => RefIds.Place("bel", "nis", "45068")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Date_keys_use_numeric_sentinels_for_open_bounds()
    {
        DateKeys.From(null).Should().Be(0);
        DateKeys.To(null).Should().Be(99991231);
        DateKeys.Of(D("2019-01-01")).Should().Be(20190101);
        DateKeys.Covers(20190101, 99991231, D("2019-01-01")).Should().BeTrue();
        DateKeys.Covers(0, 20190101, D("2019-01-01")).Should().BeFalse();
    }

    [Fact]
    public void The_ledger_mints_once_and_refuses_a_rewrite()
    {
        var ledger = new RefLedger([new LedgerEntry("nis:45068", "places/be/nis/45068", "be-refnis")]);
        ledger.Mint("be-refnis", "nis:45068", "places/be/nis/45068").Should().Be("places/be/nis/45068");
        ledger.Added.Should().BeEmpty();

        ledger.Mint("be-refnis", "nis:45069", "places/be/nis/45069");
        ledger.Added.Should().ContainSingle();

        // A source rewriting history: the rule now derives another id for a known identity.
        ledger.Mint("be-refnis", "nis:45068", "places/be/nis/99999").Should().Be("places/be/nis/45068");
        ledger.Conflicts.Should().ContainSingle(c => c.Identity == "nis:45068");
    }

    [Fact]
    public async Task A_ledger_conflict_stops_the_build_and_writes_nothing()
    {
        // RS3: a shipped ledger that maps a Belgian code elsewhere makes the whole build refuse (fail closed).
        var builder = new RefDatasetBuilder(Sources(), [new GlobalMapper2(), new MintPlayer.Spark.ReferenceData.Sources.Belgium.BelgiumMapper()]);
        var act = () => builder.BuildFixturesAsync(CancellationToken.None);
        (await act.Should().ThrowAsync<LedgerConflictException>()).Which.Conflicts.Should().Contain(c => c.Identity == "nis:45068");
    }

    /// <summary>The global mapper with a poisoned shipped ledger line for a Belgian identity.</summary>
    private sealed class GlobalMapper2 : IReferenceDataMapper
    {
        private readonly MintPlayer.Spark.ReferenceData.Sources.Global.GlobalMapper inner = new();
        public IReadOnlyList<string> Countries => inner.Countries;
        public IReadOnlyList<string> SourceIds => inner.SourceIds;
        public int Order => inner.Order;
        public IEnumerable<LedgerEntry> ShippedLedger() => inner.ShippedLedger().Append(new LedgerEntry("nis:45068", "places/be/nis/12345", "be-refnis"));
        public Task MapAsync(RefDataBuild build, IReadOnlyDictionary<string, SourceInput> inputs, CancellationToken cancellationToken) => inner.MapAsync(build, inputs, cancellationToken);
    }

    [Theory]
    [InlineData("Sint-Martens-Latem", "sintmartenslatem", "st")]
    [InlineData("'s-Gravenhage", "sgravenhage", "gravenhage")]
    [InlineData("Bruxelles", "bruxelles", "bruxelles")]
    [InlineData("Écaussinnes", "ecaussinnes", "ecaussinnes")]
    [InlineData("As", "as", "as")]
    [InlineData("Büsingen am Hochrhein", "busingenamhochrhein", "buesingen")]
    [InlineData("Onze-Lieve-Vrouw-Waver", "onzelievevrouwwaver", "olv")]
    public void Search_keys_fold_tokenize_and_add_classes(string name, string compact, string token)
    {
        var keys = RefSearchKeys.For([name]);
        keys.KeyVersion.Should().Be(RefSearchKeys.KeyVersion);
        keys.Compact.Should().Contain(compact);
        keys.Tokens.Should().Contain(token);
    }

    [Fact]
    public void A_query_folds_like_a_name()
    {
        RefSearchKeys.Query("  KRUÏSHOUTEM ").Compact.Should().Be("kruishoutem");
        RefSearchKeys.Query("Sint-Martens").Tokens.Should().Equal("sint", "martens");
        RefSearchKeys.Query("l’Église").Compact.Should().Be("leglise");
    }

    [Fact]
    public async Task Two_builds_of_the_same_inputs_give_the_same_bytes()
    {
        // RS10 (on this platform): the same inputs, the same release bytes and manifest.
        var first = await WriteAsync(await Builder().BuildFixturesAsync(CancellationToken.None));
        var second = await WriteAsync(await Builder().BuildFixturesAsync(CancellationToken.None));
        first.Files.Keys.Should().Equal(second.Files.Keys);
        foreach (var (path, bytes) in first.Files)
            Encoding.UTF8.GetString(second.Files[path]).Should().Be(Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task The_committed_seed_is_what_the_fixtures_build()
    {
        // The CI diff check of the basic seed set (RD9): --refdata-verify-seed in-process.
        var (manifest, documents, build) = await RefDataSeed.BuildAsync(Builder(), CancellationToken.None);
        build.Ledger.Added.Should().BeEmpty();
        var memory = new MemoryReleaseFileStore();
        await ReleaseWriter.WriteAsync(memory, manifest, documents, CancellationToken.None);
        var seed = Path.Combine(RepoRoot(), "apps", "ReferenceData", "ReferenceData", "Seed");
        foreach (var (path, bytes) in memory.Files)
            File.ReadAllBytes(Path.Combine(seed, path)).Should().Equal(bytes);
    }

    [Fact]
    public async Task A_release_reads_back_verified_and_refuses_dropped_ids()
    {
        var seed = await ReleaseReader.ReadAsync(new DirectoryReleaseFileStore(Path.Combine(RepoRoot(), "apps", "ReferenceData", "ReferenceData", "Seed")), CancellationToken.None);
        seed.Manifest.Release.Should().Be(RefDataSeed.Release);
        seed.OfType<RefPlace>().Should().Contain(p => p.Id == "places/be/nis/45068");

        var act = () => ReleaseReader.RefuseDroppedIds(["places/be/nis/00000"], seed);
        act.Should().Throw<ReleaseRefusedException>();

        // A tampered file is refused by its hash.
        var tampered = new MemoryReleaseFileStore();
        foreach (var file in seed.Manifest.Files)
            tampered.Files[file.Path] = File.ReadAllBytes(Path.Combine(RepoRoot(), "apps", "ReferenceData", "ReferenceData", "Seed", file.Path));
        tampered.Files["manifest.json"] = ReleaseWriter.ManifestBytes(seed.Manifest);
        var first = seed.Manifest.Files[0].Path;
        tampered.Files[first] = [.. tampered.Files[first], (byte)'\n'];
        var read = () => ReleaseReader.ReadAsync(tampered, CancellationToken.None);
        await read.Should().ThrowAsync<ReleaseRefusedException>();
    }

    private static async Task<MemoryReleaseFileStore> WriteAsync(RefDataBuild build)
    {
        var store = new MemoryReleaseFileStore();
        await ReleaseWriter.WriteAsync(store, new ReleaseManifest { Release = "test", KeyVersion = RefSearchKeys.KeyVersion, Sources = [.. build.Sources.Values] }, build.Documents(), CancellationToken.None);
        return store;
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root (nx.json) was not found.");
    }
}
