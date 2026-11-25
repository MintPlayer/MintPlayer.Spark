using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// <see cref="ISparkReadAccess"/> on the database <c>SPARK_TEST_DATABASE</c> picks (#536 M9b). It opens a record by its
/// id alone (a log inherits its related record's access, #660 G15), so it loads untyped. RavenDB finds an id in any
/// collection; a store with a table per type routes the id to the table its collection prefix names (D23 rule 2), and
/// failed with "System.Object is not a stored type" before.
/// </summary>
/// <remarks>Fixture names start with <c>Ra</c>: <c>ActionsResolver</c> matches actions classes by simple name.</remarks>
public class NeutralReadAccessTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("536b0000-0000-4000-8000-0000000000b1");

    private SparkEndpointFactory? factory;

    public override async Task DisposeAsync()
    {
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkEndpointFactory Factory => factory ??= new SparkEndpointFactory(
        Database, [NoteModel()], security: SparkTestSecurity.Permissive);

    private async Task<string> SeedAsync(object entity)
    {
        using var session = Factory.GetService<ISparkStore>().OpenSession();
        await session.StoreAsync(entity);
        await session.SaveChangesAsync();
        return session.GetId(entity)!;
    }

    private async Task<bool> MayReadAsync(string id)
    {
        using var scope = Factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISparkReadAccess>().MayReadAsync(id);
    }

    [Fact]
    public async Task A_stored_record_is_readable_by_its_id()
    {
        var id = await SeedAsync(new RaNote { Text = "Readable" });

        (await MayReadAsync(id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_record_is_not()
    {
        var id = await SeedAsync(new RaNote { Text = "Stored" });
        var missing = id[..(id.IndexOf('/') + 1)] + Guid.NewGuid();

        (await MayReadAsync(missing)).Should().BeFalse();
    }

    [Fact]
    public async Task An_id_no_collection_claims_is_not()
    {
        await SeedAsync(new RaNote { Text = "Stored" });

        (await MayReadAsync($"nosuchcollection/{Guid.NewGuid()}")).Should().BeFalse();
    }

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId, Name = "RaNote", ClrType = typeof(RaNote).FullName!, Breadcrumb = "{Text}",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Text", DataType = "string" }],
        },
    };
}

public class RaNote
{
    public string? Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class RaNoteActions : DefaultPersistentObjectActions<RaNote>
{
    public RaNoteActions(IEntityMapper entityMapper) : base(entityMapper) { }
}
