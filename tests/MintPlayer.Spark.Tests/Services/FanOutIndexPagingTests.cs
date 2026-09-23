using MintPlayer.Assertions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Paging over an index that emits several entries per document.
/// </summary>
/// <remarks>
/// ⚠️ <c>Skip(n)</c> skips index <b>entries</b>, while the caller is counting <b>documents</b> — the
/// row-security gate dedupes by id afterwards. So pushing paging into the database over a fan-out
/// index returns short pages and drifting offsets, with a total that describes neither.
/// <para>
/// The pushdown test used to ask whether an index <em>projection</em> was in play, which is a proxy
/// that does not fit: an index bound with no <c>[FromIndex]</c> projection returns the entity, passes
/// that test, and can fan out just as freely. It now asks whether any static index is bound, because
/// whether a given map fans out is not knowable — the map exists only as a rendered string.
/// </para>
/// </remarks>
public class FanOutIndexPagingTests : SparkTestDriver
{

    private static readonly Guid FanoutProbeTypeId = Guid.Parse("ffff7777-ffff-ffff-ffff-ffff77777777");

    public class FanoutProbe
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;

        /// <summary>Three per book, so the index emits three entries for every document.</summary>
        public string[] Tags { get; set; } = [];
    }

    /// <summary>A genuine fan-out: one entry per tag, not one per document.</summary>
    public class FanoutProbes_ByTag : AbstractIndexCreationTask<FanoutProbe>
    {
        public FanoutProbes_ByTag()
        {
            Map = books => from b in books
                           from tag in b.Tags
                           select new { Tag = tag, b.Title };
        }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<FanoutProbe> FanoutProbes => Session.Query<FanoutProbe>();
    }

    private SparkEndpointFactory<TestContext>? _factory;

    private static EntityTypeFile FanoutProbeModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = FanoutProbeTypeId,
            Name = "FanoutProbe",
            ClrType = typeof(FanoutProbe).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(FanoutProbe.Title), DataType = "string" },
            ],
        },
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new FanoutProbes_ByTag().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            foreach (var title in new[] { "alpha", "bravo", "charlie", "delta" })
                await session.StoreAsync(new FanoutProbe { Title = title, Tags = ["x", "y", "z"] });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private IQueryExecutor Executor()
    {
        _factory = new SparkEndpointFactory<TestContext>(
            Store, [FanoutProbeModel()],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(FanoutProbes_ByTag)));

        return _factory.GetService<IQueryExecutor>();
    }

    private static SparkQuery Query() => new()
    {
        Id = Guid.NewGuid(),
        Name = "FanoutProbes",
        Source = "Database.FanoutProbes",
        IndexName = nameof(FanoutProbes_ByTag),
    };

    /// <summary>
    /// Four documents, twelve entries. A page of two must be two documents.
    /// </summary>
    [Fact]
    public async Task A_page_over_a_fan_out_index_is_not_short()
    {
        var executor = Executor();

        var first = await executor.ExecuteQueryAsync(Query(), skip: 0, take: 2);

        first.Items.Should().HaveCount(2,
            "Skip/Take over ENTRIES would have returned fewer documents than asked for, because the " +
            "gate dedupes by id after the database has already counted three entries per book");
        first.Items.Select(i => i.Id).Distinct().Should().HaveCount(2, "and they must be different books");
    }

    /// <summary>The second page continues where the first stopped, in documents.</summary>
    [Fact]
    public async Task Offsets_over_a_fan_out_index_do_not_drift()
    {
        var executor = Executor();

        var first = await executor.ExecuteQueryAsync(Query(), skip: 0, take: 2);
        var second = await executor.ExecuteQueryAsync(Query(), skip: 2, take: 2);

        second.Items.Should().HaveCount(2, "four books, two pages of two");

        first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id))
            .Should().BeEmpty("a document on page one must not reappear on page two");
    }

    /// <summary>
    /// The count is documents, not entries — twelve would be the fan-out leaking to the caller.
    /// </summary>
    [Fact]
    public async Task TotalItems_counts_documents_not_index_entries()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(), skip: 0, take: 2);

        result.TotalItems.Should().Be(4, "four books, whatever the index emitted for them");
    }
}
