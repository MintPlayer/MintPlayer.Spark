using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Issue #293 — a context property that composes a predicate onto its query keeps that predicate when
/// the query runs against an index.
/// </summary>
/// <remarks>
/// Before the fix, <c>QueryExecutor</c> read the property's queryable and then discarded it in favour
/// of a fresh <c>session.Query&lt;TResult, TIndex&gt;()</c>, so a user-scoped grid returned every row.
/// It failed open, with no error and no log — which is why this matters more than an ordinary bug: the
/// symptom is other people's data on screen, not an exception.
/// <para>
/// Scope, stated so nobody mistakes it: this makes the <em>grid</em> honest. A by-id read or write
/// never consults the context property, so authorization still belongs in a row rule.
/// </para>
/// </remarks>
public class ScopedContextPropertyTests : SparkTestDriver
{
    private static readonly Guid AccountTypeId = Guid.Parse("aaaa1111-aaaa-aaaa-aaaa-aaaa11111111");

    private const string Me = "users/1";

    public class Account
    {
        public string? Id { get; set; }
        public string OwnerId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class Accounts_Overview : AbstractIndexCreationTask<Account>
    {
        public Accounts_Overview()
        {
            Map = accounts => from a in accounts
                              select new { a.OwnerId, a.Name };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    /// <summary>The motivating shape: a grid scoped to the signed-in user.</summary>
    public class ScopedTestContext : SparkContext
    {
        public IRavenQueryable<Account> MyAccounts => Session.Query<Account>().Where(a => a.OwnerId == Me);
        public IRavenQueryable<Account> AllAccounts => Session.Query<Account>();
    }

    private static EntityTypeFile AccountModel(string? indexName) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = AccountTypeId,
            Name = "Account",
            ClrType = typeof(Account).FullName!,
            IndexName = indexName,
            Breadcrumb = "{Name}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "OwnerId", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
            ],
        },
    };

    private async Task<SparkEndpointFactory<ScopedTestContext>> CreateFactoryAsync(string? indexName)
    {
        await new Accounts_Overview().ExecuteAsync(Store);

        var factory = new SparkEndpointFactory<ScopedTestContext>(Store, [AccountModel(indexName)],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(Accounts_Overview)));

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Account { OwnerId = Me, Name = "mine-a" });
            await session.StoreAsync(new Account { OwnerId = Me, Name = "mine-b" });
            await session.StoreAsync(new Account { OwnerId = "users/2", Name = "theirs" });
        });
        await Store.WaitForIndexingAsync();

        return factory;
    }

    private static SparkQuery Query(string source) => new()
    {
        Id = Guid.NewGuid(),
        Name = "GetAccounts",
        Source = source,
    };

    [Fact]
    public async Task A_filtered_context_property_keeps_its_predicate_under_an_index_binding()
    {
        // RED before the fix: returns all three rows, including another user's.
        await using var factory = await CreateFactoryAsync(indexName: "Accounts_Overview");
        var executor = factory.GetService<IQueryExecutor>();

        var result = await executor.ExecuteQueryAsync(Query("Database.MyAccounts"));

        result.TotalRecords.Should().Be(2);
        result.Data
            .Select(po => po.Attributes.Single(a => a.Name == "Name").Value?.ToString())
            .Should().BeEquivalentTo(["mine-a", "mine-b"]);
    }

    [Fact]
    public async Task A_filtered_context_property_without_an_index_binding_still_filters()
    {
        // The non-index path was never broken; this pins that the fix did not disturb it.
        await using var factory = await CreateFactoryAsync(indexName: null);
        var executor = factory.GetService<IQueryExecutor>();

        var result = await executor.ExecuteQueryAsync(Query("Database.MyAccounts"));

        result.TotalRecords.Should().Be(2);
    }

    [Fact]
    public async Task A_bare_context_property_under_an_index_binding_is_unchanged()
    {
        // The no-regression floor: every context property in the repo and in the demos is a bare
        // root, including the ones the index generator emits, so this is the broad case.
        await using var factory = await CreateFactoryAsync(indexName: "Accounts_Overview");
        var executor = factory.GetService<IQueryExecutor>();

        var result = await executor.ExecuteQueryAsync(Query("Database.AllAccounts"));

        result.TotalRecords.Should().Be(3);
    }

    [Fact]
    public async Task A_bare_context_property_without_an_index_binding_is_unchanged()
    {
        await using var factory = await CreateFactoryAsync(indexName: null);
        var executor = factory.GetService<IQueryExecutor>();

        var result = await executor.ExecuteQueryAsync(Query("Database.AllAccounts"));

        result.TotalRecords.Should().Be(3);
    }
}
