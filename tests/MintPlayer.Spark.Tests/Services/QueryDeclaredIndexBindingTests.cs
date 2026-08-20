using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The #272 motivating case, finally without a tiebreaker (#279): two indexes coexist over one
/// entity — the [DefaultIndex]-elected overview and a specialized hand-written index — and each
/// Spark query declares which one it runs against. No ambient resolution, no ordinal-min.
/// </summary>
public class QueryDeclaredIndexBindingTests : SparkTestDriver
{
    private static readonly Guid CommitTypeId = Guid.Parse("cccc3333-cccc-cccc-cccc-cccc33333333");

    public class Commit
    {
        public string? Id { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Commit> Commits => Session.Query<Commit>();
    }

    /// <summary>The generic-surface index — the model-shaping default.</summary>
    [DefaultIndex]
    public class Commits_Overview : AbstractIndexCreationTask<Commit>
    {
        public Commits_Overview()
        {
            Map = commits => from c in commits
                             select new { c.Message, c.Repository, c.Author };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(Commits_Overview))]
    public class VCommit
    {
        public string? Id { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
    }

    /// <summary>A specialized coexisting index whose projection computes a field the entity lacks.</summary>
    public class Commits_ByRepository : AbstractIndexCreationTask<Commit>
    {
        public Commits_ByRepository()
        {
            Map = commits => from c in commits
                             select new { c.Repository, Summary = c.Repository + ": " + c.Message };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(Commits_ByRepository))]
    public class VCommitByRepository
    {
        public string? Id { get; set; }
        public string Repository { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
    }

    private static EntityTypeFile CommitModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CommitTypeId,
            Name = "Commit",
            ClrType = typeof(Commit).FullName!,
            QueryType = typeof(VCommit).FullName,
            IndexName = "Commits_Overview",
            Breadcrumb = "{Message}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Message", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Repository", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Author", DataType = "string" },
                // Projection-only on the specialized index; null on rows served by the default.
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Summary", DataType = "string" },
            ],
        },
    };

    private SparkEndpointFactory<TestContext> _factory = null!;
    private IQueryExecutor _executor = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await new Commits_Overview().ExecuteAsync(Store);
        await new Commits_ByRepository().ExecuteAsync(Store);

        _factory = new SparkEndpointFactory<TestContext>(Store, [CommitModel()],
            configureIndexCatalog: catalog =>
            {
                catalog.RegisterIndex(typeof(Commits_Overview));
                catalog.RegisterIndex(typeof(Commits_ByRepository));
                catalog.RegisterProjection(typeof(VCommit), typeof(Commits_Overview));
                catalog.RegisterProjection(typeof(VCommitByRepository), typeof(Commits_ByRepository));
            });
        _executor = _factory.GetService<IQueryExecutor>();

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Commit { Message = "init", Repository = "spark", Author = "ada" });
            await session.StoreAsync(new Commit { Message = "fix", Repository = "spark", Author = "grace" });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static SparkQuery Query(string name, string? indexName) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Source = "Database.Commits",
        IndexName = indexName,
    };

    [Fact]
    public async Task Each_query_runs_against_its_declared_index()
    {
        var overview = await _executor.ExecuteQueryAsync(Query("GetCommits", "Commits_Overview"));
        var byRepository = await _executor.ExecuteQueryAsync(Query("GetCommitsByRepository", "Commits_ByRepository"));

        overview.TotalRecords.Should().Be(2);
        byRepository.TotalRecords.Should().Be(2);

        // The specialized projection computes Summary; the default projection does not have it.
        byRepository.Data
            .Select(po => po.Attributes.Single(a => a.Name == "Summary").Value?.ToString())
            .Should().BeEquivalentTo(["spark: init", "spark: fix"]);
    }

    /// <summary>
    /// SP279.3 pin: one merged grid shape per entity degrades gracefully. A column the query's
    /// projection lacks null-fills — no throw, no missing attribute — until per-query grid shapes
    /// exist as their own feature.
    /// </summary>
    [Fact]
    public async Task A_column_missing_from_the_declared_projection_null_fills()
    {
        var overview = await _executor.ExecuteQueryAsync(Query("GetCommits", "Commits_Overview"));
        var byRepository = await _executor.ExecuteQueryAsync(Query("GetCommitsByRepository", "Commits_ByRepository"));

        overview.Data.Should().OnlyContain(po =>
            po.Attributes.Single(a => a.Name == "Summary").Value == null,
            "the default projection has no Summary");
        byRepository.Data.Should().OnlyContain(po =>
            po.Attributes.Single(a => a.Name == "Author").Value == null,
            "the specialized projection has no Author");
    }

    /// <summary>
    /// SP279.1 pin: sorting on a column the projection lacks drops the column (with a console
    /// warning) instead of throwing — and sorting on a shared column works on either index.
    /// </summary>
    [Fact]
    public async Task Sorting_on_a_projection_missing_column_degrades_instead_of_throwing()
    {
        var query = Query("GetCommitsByRepository", "Commits_ByRepository");
        query.SortColumns = [new SortColumn { Property = "Author", Direction = "asc" }];

        var act = () => _executor.ExecuteQueryAsync(query);

        (await act.Should().NotThrowAsync()).Which.TotalRecords.Should().Be(2);
    }
}
