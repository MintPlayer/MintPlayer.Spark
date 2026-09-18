using Xunit;
using CodeCoverage.Entities;
using CodeCoverage.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// The migration that converts stored branch edges to the per-line arm-set shape.
/// </summary>
/// <remarks>
/// Its patch is a JavaScript string, so nothing in the build checks it. This does —
/// including the case that only showed up when it was rehearsed against a copy of
/// production: a document with more edges than a patch script's statement budget.
/// </remarks>
public class BranchesBecomePerLineArmSetsMigrationTests : CoverageRavenTest
{
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

    private static async Task SeedAsync(IDocumentStore store, string id, LegacyFileCoverage legacy)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(legacy, id);
        session.Advanced.GetMetadataFor(legacy)["@collection"] = "FileCoverages";
        await session.SaveChangesAsync();
    }

    private static Task RunAsync(IDocumentStore store)
        => new M_202609190900_BranchesBecomePerLineArmSets(store).UpAsync(CancellationToken.None);

    private static async Task<FileCoverage> LoadAsync(IDocumentStore store, string id)
    {
        using var session = store.OpenAsyncSession();
        // Loaded WITHOUT the compatibility hook, so this asserts what is actually
        // stored rather than what the hook would derive on the way out.
        return await session.LoadAsync<FileCoverage>(id);
    }

    [Fact]
    public async Task Lcov_edges_become_named_arms()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "files/1", new LegacyFileCoverage
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
        });

        await RunAsync(store);

        var branches = (await LoadAsync(store, "files/1")).Branches.Single();
        branches.Line.Should().Be(24);
        branches.Arity.Should().Be(2);
        branches.TakenArms.Should().BeEquivalentTo(["0:0"]);
        branches.Floor.Should().Be(0);
    }

    [Fact]
    public async Task Cobertura_edges_become_a_floor()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "files/2", new LegacyFileCoverage
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
        });

        await RunAsync(store);

        var branches = (await LoadAsync(store, "files/2")).Branches.Single();
        branches.Arity.Should().Be(2);
        branches.Floor.Should().Be(1);
        branches.TakenArms.Should().BeEmpty(
            "cobertura's stored ids were positional fiction, never arm identities");
    }

    [Fact]
    public async Task A_document_with_more_edges_than_the_script_statement_budget_still_converts()
    {
        // Measured against a copy of production: ~450 edges exhausts a patch
        // script's 10,000-statement budget, production holds ~770 documents above
        // 400 edges and ten at 5,263, and a faulted patch aborts startup. This is
        // the case IgnoreMaxStepsForScript exists for.
        const int edges = 6000;
        var legacy = new LegacyFileCoverage
        {
            BuildId = "b",
            Path = "src/big.ts",
            BranchFormat = "lcov",
            Lines = [new LegacyLine { Number = 1, Hits = 1, Status = LineStatus.PartiallyCovered }],
        };
        for (var i = 0; i < edges; i++)
        {
            legacy.Branches.Add(new LegacyBranch
            {
                Line = 1 + (i / 4),
                BlockId = "0",
                BranchId = i.ToString(),
                Taken = i % 2,
            });
        }

        using var store = GetDocumentStore();
        await SeedAsync(store, "files/big", legacy);

        await RunAsync(store);

        var file = await LoadAsync(store, "files/big");
        file.Branches.Sum(b => b.Arity).Should().Be(edges, "no edge may be dropped");
        file.Branches.Sum(b => b.TakenArms.Count).Should().Be(edges / 2);
        file.Branches.Should().BeInAscendingOrder(b => b.Line);
    }

    [Fact]
    public async Task Running_twice_changes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "files/3", new LegacyFileCoverage
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
        });

        await RunAsync(store);
        var once = (await LoadAsync(store, "files/3")).Branches.Single();

        await RunAsync(store);
        var twice = (await LoadAsync(store, "files/3")).Branches.Single();

        twice.Should().BeEquivalentTo(once);
    }

    [Fact]
    public async Task A_document_without_branches_is_left_alone_but_loses_its_stamp()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "files/4", new LegacyFileCoverage
        {
            BuildId = "b",
            Path = "src/a.ts",
            Lines = [new LegacyLine { Number = 1, Hits = 1, Status = LineStatus.Covered }],
            Branches = [],
        });

        await RunAsync(store);

        var file = await LoadAsync(store, "files/4");
        file.Branches.Should().BeEmpty();
        file.Lines.Should().ContainSingle();
    }

    [Fact]
    public async Task Every_other_field_survives()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, "files/5", new LegacyFileCoverage
        {
            BuildId = "Commits/1/abc/builds/9-1",
            Path = "src/a.ts",
            Matched = true,
            BranchFormat = "lcov",
            Lines =
            [
                new LegacyLine { Number = 1, Hits = 2, Status = LineStatus.Covered },
                new LegacyLine { Number = 24, Hits = null, Status = LineStatus.PartiallyCovered },
            ],
            Branches = [new LegacyBranch { Line = 24, BlockId = "0", BranchId = "0", Taken = 1 }],
        });

        await RunAsync(store);

        var file = await LoadAsync(store, "files/5");
        file.BuildId.Should().Be("Commits/1/abc/builds/9-1");
        file.Path.Should().Be("src/a.ts");
        file.Matched.Should().BeTrue();
        file.Lines.Should().HaveCount(2);
        file.Lines[0].Hits.Should().Be(2);
        file.Lines[1].Hits.Should().NotHaveValue();
    }
}
