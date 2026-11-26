using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// A grid row's etag for a document stored before typed ids (#536 D7). Spark used to name a new document after its
/// collection's casing (<c>LcItems/…</c>); a typed id loads lower case, while RavenDB hands the version back under the
/// stored casing. A row without its etag can't be deleted from the grid (#467 D14: a missing etag is a 400).
/// </summary>
public class QueryRowEtagCasingTests : SparkTestDriver
{
    private static readonly Guid ItemTypeId = Guid.Parse("536e0000-0000-4000-8000-0000000000e1");

    public class LcItem
    {
        public Id<LcItem> Id { get; set; }
        public string Name { get; set; } = string.Empty;

        internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = Id<LcItem>.FromTrusted(value); }
    }

    public class LcContext : SparkContext
    {
        public IQueryable<LcItem> LcItems => Session.Query<LcItem>();
    }

    private static EntityTypeFile Model() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ItemTypeId,
            Name = "LcItem",
            ClrType = typeof(LcItem).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };

    [Fact]
    public async Task A_row_stored_under_its_pre_upgrade_casing_carries_its_etag()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new LcItem { Name = "legacy" }, "LcItems/LEGACY");
            await session.SaveChangesAsync();
        }
        await using var factory = new SparkEndpointFactory<LcContext>(Store, [Model()]);

        using var scope = factory.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IQueryExecutor>().ExecuteQueryAsync(new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "LcItems",
            Source = "Database.LcItems",
        });

        var row = result.Items.Single();
        row.Id.Should().Be("lcitems/legacy");
        row.Etag.Should().NotBeNullOrEmpty();
    }
}
