using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Raven.Client.Documents;



namespace CodeCoverage.Tests.Model;

/// <summary>
/// The guarantee: a report that opened before #420 deployed still opens after
/// it, whether or not the migration has run. These store documents in the
/// pre-#420 shape and load them as <see cref="FileCoverage"/> through a store
/// with the compatibility hook attached.
/// </summary>
public class LegacyBranchCompatibilityTests : CoverageRavenTest
{
    /// <summary>The pre-#420 document shape, exactly as production still holds it.</summary>
    private class LegacyFileCoverage
    {
        public string BuildId { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Matched { get; set; } = true;
        public string? BranchFormat { get; set; }
        public List<LegacyLine> Lines { get; set; } = [];
        public List<LegacyBranch> Branches { get; set; } = [];
    }

    private class LegacyLine
    {
        public int Number { get; set; }
        public int? Hits { get; set; }
        public LineStatus Status { get; set; }
    }

    private class LegacyBranch
    {
        public int Line { get; set; }
        public string BlockId { get; set; } = "";
        public string BranchId { get; set; } = "";
        public int? Taken { get; set; }
    }

    private async Task<IDocumentStore> StoreWithLegacyDocumentAsync(LegacyFileCoverage legacy, string id)
    {
        var store = GetDocumentStore();
        LegacyBranchCompatibility.Enable(store);

        using var session = store.OpenAsyncSession();
        await session.StoreAsync(legacy, id);
        // Land it in the collection FileCoverage reads from, as production has it.
        session.Advanced.GetMetadataFor(legacy)["@collection"] = "FileCoverages";
        await session.SaveChangesAsync();
        return store;
    }

    [Fact]
    public async Task An_lcov_document_keeps_its_arm_identity()
    {
        var legacy = new LegacyFileCoverage
        {
            BuildId = "b",
            Path = "src/a.ts",
            BranchFormat = "lcov",
            Lines = [new LegacyLine { Number = 24, Hits = 5, Status = LineStatus.PartiallyCovered }],
            Branches =
            [
                new LegacyBranch { Line = 24, BlockId = "0", BranchId = "0", Taken = 5 },
                new LegacyBranch { Line = 24, BlockId = "0", BranchId = "1", Taken = 0 },
            ],
        };

        using var store = await StoreWithLegacyDocumentAsync(legacy, "files/1");
        using var session = store.OpenAsyncSession();
        var file = await session.LoadAsync<FileCoverage>("files/1");

        var branches = file.Branches.Single();
        branches.Line.Should().Be(24);
        branches.Arity.Should().Be(2);
        // lcov ids are real arm identities.
        branches.TakenArms.Should().BeEquivalentTo(["0:0"]);
        branches.Floor.Should().Be(0);
        branches.Covered.Should().Be(1);
        branches.IsPartial.Should().BeTrue();
    }

    [Fact]
    public async Task A_cobertura_document_becomes_a_floor_with_no_named_arms()
    {
        var legacy = new LegacyFileCoverage
        {
            BuildId = "b",
            Path = "src/a.cs",
            BranchFormat = "cobertura",
            Lines = [new LegacyLine { Number = 12, Hits = 1, Status = LineStatus.PartiallyCovered }],
            Branches =
            [
                new LegacyBranch { Line = 12, BlockId = "0", BranchId = "0", Taken = 1 },
                new LegacyBranch { Line = 12, BlockId = "0", BranchId = "1", Taken = 0 },
            ],
        };

        using var store = await StoreWithLegacyDocumentAsync(legacy, "files/2");
        using var session = store.OpenAsyncSession();
        var file = await session.LoadAsync<FileCoverage>("files/2");

        var branches = file.Branches.Single();
        branches.Arity.Should().Be(2);
        branches.Floor.Should().Be(1);
        branches.TakenArms.Should().BeEmpty(
            "those ids were positional fiction for a (covered/total) count, not arm identities");
        branches.Covered.Should().Be(1);
    }

    [Fact]
    public async Task The_totals_a_legacy_document_reports_are_unchanged()
    {
        // What the user sees on the file page must not move just because the
        // storage shape did.
        var legacy = new LegacyFileCoverage
        {
            BuildId = "b",
            Path = "src/a.ts",
            BranchFormat = "lcov",
            Lines =
            [
                new LegacyLine { Number = 1, Hits = 2, Status = LineStatus.Covered },
                new LegacyLine { Number = 24, Hits = 5, Status = LineStatus.PartiallyCovered },
            ],
            Branches =
            [
                new LegacyBranch { Line = 24, BlockId = "0", BranchId = "0", Taken = 5 },
                new LegacyBranch { Line = 24, BlockId = "0", BranchId = "1", Taken = 0 },
                new LegacyBranch { Line = 24, BlockId = "0", BranchId = "2", Taken = 3 },
            ],
        };

        using var store = await StoreWithLegacyDocumentAsync(legacy, "files/3");
        using var session = store.OpenAsyncSession();
        var file = await session.LoadAsync<FileCoverage>("files/3");

        var summary = CodeCoverage.Ingestion.CoverageMerger.Summarize([file]);
        summary.BranchesTotal.Should().Be(3, "three edges were stored, so the line has three arms");
        summary.BranchesCovered.Should().Be(2, "two of them were taken");
        summary.LinesCoverable.Should().Be(2);
        summary.LinesCovered.Should().Be(2);
    }

    [Fact]
    public async Task A_document_with_no_branches_is_left_alone()
    {
        var legacy = new LegacyFileCoverage
        {
            BuildId = "b",
            Path = "src/a.ts",
            Lines = [new LegacyLine { Number = 1, Hits = 1, Status = LineStatus.Covered }],
            Branches = [],
        };

        using var store = await StoreWithLegacyDocumentAsync(legacy, "files/4");
        using var session = store.OpenAsyncSession();
        var file = await session.LoadAsync<FileCoverage>("files/4");

        file.Branches.Should().BeEmpty();
        file.Lines.Should().ContainSingle();
    }

    [Fact]
    public async Task A_document_already_in_the_new_shape_is_untouched()
    {
        var store = GetDocumentStore();
        LegacyBranchCompatibility.Enable(store);

        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new FileCoverage
            {
                BuildId = "b",
                Path = "src/a.ts",
                Branches = [new LineBranchCoverage { Line = 7, Arity = 2, Floor = 0, TakenArms = ["0:0"] }],
            }, "files/5");
            await seed.SaveChangesAsync();
        }

        using var session = store.OpenAsyncSession();
        var file = await session.LoadAsync<FileCoverage>("files/5");

        var branches = file.Branches.Single();
        branches.Arity.Should().Be(2);
        branches.TakenArms.Should().BeEquivalentTo(["0:0"]);
        store.Dispose();
    }
}
