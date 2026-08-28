using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Issue #295 — a sort column must name an attribute of the type's query surface.
/// </summary>
/// <remarks>
/// Not about <em>who may query the collection</em> — that is the type-level right, already settled by
/// <c>EnsureAuthorizedAsync("Query", ...)</c> long before any of this runs, and every caller here is
/// properly authorized to query the type. What is at stake is narrower: an authorized caller ordering
/// by an attribute they are not allowed to <em>read</em>.
/// <para>
/// Ordering is a comparison oracle. Redaction nulls a value in the response but leaves the
/// <c>ORDER BY</c> intact, so before this gate an attribute the caller could never read was still
/// recoverable one comparison at a time: sort ascending, sort descending, observe where the row
/// lands, bisect. The disclosure is silent and leaves no trace distinguishable from ordinary paging.
/// <para>
/// Every assertion here is on the emitted RQL. Row order cannot prove the absence of a sort — a
/// collection may happen to come back in the requested order — whereas an absent <c>order by</c>
/// clause is unambiguous.
/// </para>
/// </remarks>
public class SortColumnDisclosureTests : SparkTestDriver
{
    private static readonly Guid VaultTypeId = Guid.Parse("eeee5555-eeee-eeee-eeee-eeee55555555");

    public class Vault
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;

        /// <summary>Stands in for a redacted attribute — hidden from the grid, present on the document.</summary>
        public string SecretToken { get; set; } = string.Empty;

        /// <summary>Never declared in the model at all.</summary>
        public int InternalRank { get; set; }
    }

    public class Vaults_Overview : AbstractIndexCreationTask<Vault>
    {
        public Vaults_Overview()
        {
            Map = vaults => from v in vaults
                            select new { v.Label, v.SecretToken, v.InternalRank };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Vault> Vaults => Session.Query<Vault>();
    }

    /// <summary>
    /// <c>SecretToken</c> is modelled but narrowed to the detail view — exactly what an app does to
    /// keep an attribute off the grid. <c>InternalRank</c> is not modelled at all.
    /// </summary>
    private static EntityTypeFile VaultModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = VaultTypeId,
            Name = "Vault",
            ClrType = typeof(Vault).FullName!,
            Breadcrumb = "{Label}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Label", DataType = "string" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "SecretToken",
                    DataType = "string",
                    ShowedOn = EShowedOn.PersistentObject,
                },
            ],
        },
    };

    private SparkEndpointFactory<TestContext> _factory = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new Vaults_Overview().ExecuteAsync(Store);
        _factory = new SparkEndpointFactory<TestContext>(Store, [VaultModel()],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(Vaults_Overview)));

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Vault { Label = "beta", SecretToken = "aaa", InternalRank = 3 });
            await session.StoreAsync(new Vault { Label = "alpha", SecretToken = "zzz", InternalRank = 1 });
            await session.StoreAsync(new Vault { Label = "gamma", SecretToken = "mmm", InternalRank = 2 });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static SparkQuery Query(params SortColumn[] sortColumns) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Vaults",
        Source = "Database.Vaults",
        SortColumns = sortColumns,
    };

    private (IQueryExecutor Executor, RqlRecorder Rql) Capture()
    {
        var recorder = RqlRecorder.Attach(Store);
        return (_factory.GetService<IQueryExecutor>(), recorder);
    }

    [Fact]
    public async Task A_sort_column_hidden_from_the_query_surface_is_refused()
    {
        // RED before the fix: ApplySorting resolved SecretToken by reflection and emitted
        // `order by SecretToken`, ordering rows by a value the grid never shows.
        var (executor, rql) = Capture();
        using var _rql = rql;

        var result = await executor.ExecuteQueryAsync(
            Query(new SortColumn { Property = "SecretToken", Direction = "asc" }));

        result.TotalItems.Should().Be(3);
        rql.Should().ContainSingle().Which.Should().NotContain("order by");
    }

    [Fact]
    public async Task A_sort_column_absent_from_the_model_is_refused()
    {
        // InternalRank exists on the CLR type and in the index, but is not a modelled attribute.
        var (executor, rql) = Capture();
        using var _rql = rql;

        var result = await executor.ExecuteQueryAsync(
            Query(new SortColumn { Property = "InternalRank", Direction = "asc" }));

        result.TotalItems.Should().Be(3);
        rql.Should().ContainSingle().Which.Should().NotContain("order by");
    }

    [Fact]
    public async Task A_sort_column_on_the_query_surface_still_sorts()
    {
        // The floor: the gate must not break ordinary sorting.
        var (executor, rql) = Capture();
        using var _rql = rql;

        var result = await executor.ExecuteQueryAsync(
            Query(new SortColumn { Property = "Label", Direction = "asc" }));

        rql.Should().ContainSingle().Which.Should().Contain("order by Label");
        result.Items
            .Select(po => po.Values.Single(a => a.Key == "Label").Value?.ToString())
            .Should().Equal("alpha", "beta", "gamma");
    }

    [Fact]
    public async Task A_refused_sort_column_does_not_suppress_an_allowed_one()
    {
        // A rejected column is dropped, not fatal, and must not take its neighbours with it —
        // otherwise a client persisting a stale sort state loses ordering entirely.
        var (executor, rql) = Capture();
        using var _rql = rql;

        await executor.ExecuteQueryAsync(Query(
            new SortColumn { Property = "SecretToken", Direction = "asc" },
            new SortColumn { Property = "Label", Direction = "asc" }));

        var emitted = rql.Should().ContainSingle().Subject;
        emitted.Should().Contain("order by Label");
        emitted.Should().NotContain("SecretToken");
    }
}
