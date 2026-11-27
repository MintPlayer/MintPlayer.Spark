using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.History;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.History;

/// <summary>
/// #536 S8: History and SoftDelete on the database <c>SPARK_TEST_DATABASE</c> picks, through <see cref="ISparkRevisions"/>.
/// Revisions are configured from the model at startup, listed newest first with who wrote them, read back, and reverted
/// through the save pipeline; a soft delete restores, and a purge takes the row's revisions with it. The RavenDB-only
/// details (its configuration document, licence caps, change-vector scoping) stay in <see cref="HistoryTests"/>.
/// </summary>
/// <remarks>Fixture names start with <c>Hn</c>: <c>ActionsResolver</c> matches actions classes by simple name.</remarks>
public class NeutralHistoryStoreTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("46030000-0000-4000-8000-0000000000a1");
    private const string Alice = "users/alice";
    private const string Bob = "users/bob";

    private SparkEndpointFactory? factory;
    private SparkClient? client;
    private readonly HnIdentity identity = new() { Id = Alice };

    public override async Task DisposeAsync()
    {
        client?.Dispose();
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkClient Start()
    {
        factory = new SparkEndpointFactory(
            Database,
            [NoteModel()],
            configureServices: s =>
            {
                s.AddSingleton(identity);
                s.AddScoped<ISparkCurrentPrincipal, HnPrincipal>();
            },
            configureSpark: spark =>
            {
                spark.AddSoftDelete();
                spark.AddHistory();
            },
            security: SparkTestSecurity.Permissive);
        client = new SparkClient(factory.CreateClient(), ownsClient: true);
        return client;
    }

    private ISparkStore Store => factory!.GetService<ISparkStore>();

    private async Task<HnNote?> ReadAsync(string id)
    {
        using var session = Store.OpenSession();
        return await session.LoadAsync<HnNote>(id);
    }

    private async Task<T> AsAsync<T>(Func<IServiceProvider, Task<T>> act)
    {
        using var scope = factory!.CreateScope();
        return await act(scope.ServiceProvider);
    }

    private static PersistentObject NewNote(string title) => new()
    {
        Name = "HnNote",
        ObjectTypeId = NoteTypeId,
        Attributes = [new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = title, IsValueChanged = true }],
    };

    private async Task<string> AliceWritesThenBobEditsAsync(SparkClient client)
    {
        var created = await client.CreatePersistentObjectAsync(NewNote("one"));
        identity.Id = Bob;
        var loaded = (await client.GetPersistentObjectAsync(NoteTypeId, created.Id!))!;
        loaded["Title"].Value = "two";
        loaded["Title"].IsValueChanged = true;
        await client.UpdatePersistentObjectAsync(loaded);
        return created.Id!;
    }

    [Fact]
    public async Task Revisions_are_listed_newest_first_with_who_wrote_them_and_read_back()
    {
        var client = Start();
        var id = await AliceWritesThenBobEditsAsync(client);

        var revisions = await AsAsync(sp => sp.GetRequiredService<ISparkHistory>().ListAsync(NoteTypeId, id));

        revisions.Should().HaveCount(2);
        revisions[0].UserId.Should().Be(Bob);
        revisions[0].IsCurrent.Should().BeTrue();
        revisions[1].UserId.Should().Be(Alice);
        revisions[1].IsCurrent.Should().BeFalse();

        var first = await AsAsync(sp => sp.GetRequiredService<ISparkHistory>().GetAsync(NoteTypeId, id, revisions[1].ChangeVector));
        first["Title"].Value?.ToString().Should().Be("one");
    }

    [Fact]
    public async Task A_revert_writes_the_revisions_values_through_the_save_pipeline()
    {
        var client = Start();
        var id = await AliceWritesThenBobEditsAsync(client);
        var oldest = (await AsAsync(sp => sp.GetRequiredService<ISparkHistory>().ListAsync(NoteTypeId, id)))[^1];

        await AsAsync(sp => sp.GetRequiredService<ISparkHistory>().RevertAsync(NoteTypeId, id, oldest.ChangeVector));

        var reverted = await ReadAsync(id);
        reverted!.Title.Should().Be("one");
        reverted.CreatedBy.Should().Be(Alice, "CreatedBy is immutable");
        reverted.ModifiedBy.Should().Be(Bob, "a revert is a write by its caller");
        (await AsAsync(sp => sp.GetRequiredService<ISparkHistory>().ListAsync(NoteTypeId, id))).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_soft_delete_restores_and_a_purge_takes_the_revisions_with_the_row()
    {
        var client = Start();
        var created = await client.CreatePersistentObjectAsync(NewNote("gone"));
        var id = created.Id!;
        await client.DeletePersistentObjectAsync(created);

        (await ReadAsync(id))!.IsDeleted.Should().BeTrue("a delete of a soft-deletable row marks it");
        (await AsAsync(sp => sp.GetRequiredService<ISparkSoftDelete>().RestoreAsync(NoteTypeId, id))).Should().NotBeNull();
        (await ReadAsync(id))!.IsDeleted.Should().BeFalse();

        // Only a deleted row can be purged (SoftDeleteRowPolicy).
        await client.DeletePersistentObjectAsync((await client.GetPersistentObjectAsync(NoteTypeId, id))!);
        await AsAsync(async sp => { await sp.GetRequiredService<ISparkSoftDelete>().PurgeAsync(NoteTypeId, id); return 0; });

        (await ReadAsync(id)).Should().BeNull();
        (await factory!.GetService<ISparkRevisions>().ListAsync(typeof(HnNote), id, 0, 50)).Should().BeEmpty("a purge takes every revision");
    }

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "HnNote",
            ClrType = typeof(HnNote).FullName!,
            Breadcrumb = "{Title}",
            Revisions = new EntityRevisionsDefinition { Enabled = true },
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string" }],
        },
    };
}

public class HnNote : IAuditable, ISoftDeletable
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

public sealed class HnIdentity
{
    public string? Id { get; set; }
}

public sealed class HnPrincipal(HnIdentity identity) : ISparkCurrentPrincipal
{
    public string? Id => identity.Id;
    public bool IsAuthenticated => identity.Id is not null;
}
