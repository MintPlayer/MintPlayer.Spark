using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Contributions F6 — a refused save or delete evicts every document written in the request session
/// during the operation, not only its target. Before, an interceptor's side document (a contribution,
/// a recomputed current document) stayed tracked after WITH CHECK or a later hook refused, and the
/// request's next <c>SaveChangesAsync</c> — here, a second, allowed save in the same scope — committed
/// it, orphaned from the save that was refused.
/// </summary>
/// <remarks>Fixture names start with <c>Ev</c>: ActionsResolver matches actions classes by simple name across the assembly.</remarks>
public class RefusedWriteEvictionTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("e6f60000-0000-4000-8000-e6f600000001");

    private SparkEndpointFactory<EvContext> factory = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        factory = new SparkEndpointFactory<EvContext>(
            Store,
            [NoteModel()],
            configureServices: services => services.AddScoped<EvNoteActions>(),
            configureSpark: spark => spark.AddInterceptor<EvSideWritingInterceptor>());
    }

    public override async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private Task SeedAsync(params (string Id, string Title)[] notes) => SeedAsync(async session =>
    {
        foreach (var (id, title) in notes)
            await session.StoreAsync(new EvNote { Title = title }, id);
    });

    private async Task<bool> ExistsAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Advanced.ExistsAsync(id);
    }

    private async Task<string?> TitleAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<EvNote>(id))?.Title;
    }

    private static PersistentObject Edit(string id, string title)
    {
        var po = new PersistentObject { Id = id, ObjectTypeId = NoteTypeId, Name = "EvNote" };
        po.AddAttribute(new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = title, IsValueChanged = true });
        return po;
    }

    [Fact]
    public async Task A_save_refused_by_WITH_CHECK_does_not_leave_the_interceptors_side_document_for_the_next_save()
    {
        await SeedAsync(("EvNotes/1", "one"), ("EvNotes/2", "two"));

        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

            var refused = async () => await db.SavePersistentObjectAsync(Edit("EvNotes/1", EvNoteActions.Forbidden));
            await refused.Should().ThrowAsync<SparkRowLevelAccessDeniedException>();

            // The same request scope — the same session — goes on to an allowed save, which commits.
            await db.SavePersistentObjectAsync(Edit("EvNotes/2", "allowed"));
        }

        (await ExistsAsync($"EvSides/saved/{EvNoteActions.Forbidden}")).Should().BeFalse(
            "the side document belongs to the refused save; the next SaveChangesAsync must not commit it");
        (await ExistsAsync("EvSides/saved/allowed")).Should().BeTrue("the allowed save's own side document is written");
        (await TitleAsync("EvNotes/1")).Should().Be("one", "the refused target is untouched");
        (await TitleAsync("EvNotes/2")).Should().Be("allowed");
    }

    [Fact]
    public async Task A_refused_save_leaves_changes_that_were_pending_before_it()
    {
        await SeedAsync(("EvNotes/1", "one"), ("EvNotes/2", "two"));

        using (var scope = factory.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IAsyncDocumentSession>();
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

            // Earlier in the request, something changed a document and has not saved yet.
            (await session.LoadAsync<EvNote>("EvNotes/2")).Title = "pending";

            var refused = async () => await db.SavePersistentObjectAsync(Edit("EvNotes/1", EvNoteActions.Forbidden));
            await refused.Should().ThrowAsync<SparkRowLevelAccessDeniedException>();

            await session.SaveChangesAsync();
        }

        (await TitleAsync("EvNotes/2")).Should().Be("pending", "a change made before the refused save is not the refused save's to take back");
        (await ExistsAsync($"EvSides/saved/{EvNoteActions.Forbidden}")).Should().BeFalse();
    }

    [Fact]
    public async Task A_delete_refused_by_an_interceptor_does_not_leave_its_side_document_for_the_next_delete()
    {
        await SeedAsync(("EvNotes/3", EvSideWritingInterceptor.Locked), ("EvNotes/4", "four"));

        using (var scope = factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

            var refused = async () => await db.DeletePersistentObjectAsync(NoteTypeId, "EvNotes/3");
            await refused.Should().ThrowAsync<InvalidOperationException>();

            await db.DeletePersistentObjectAsync(NoteTypeId, "EvNotes/4");
        }

        (await ExistsAsync($"EvSides/deleted/{EvSideWritingInterceptor.Locked}")).Should().BeFalse(
            "the side document belongs to the refused delete; the next SaveChangesAsync must not commit it");
        (await ExistsAsync("EvSides/deleted/four")).Should().BeTrue("the allowed delete's own side document is written");
        (await ExistsAsync("EvNotes/3")).Should().BeTrue("the refused delete deleted nothing");
        (await ExistsAsync("EvNotes/4")).Should().BeFalse();
    }

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "EvNote",
            ClrType = typeof(EvNote).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string", IsVisible = true },
            ],
        },
    };
}

public class EvNote
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class EvSide
{
    public string? Id { get; set; }
    public string Note { get; set; } = string.Empty;
}

public class EvContext : SparkContext
{
    public IRavenQueryable<EvNote> Notes => Session.Query<EvNote>();
}

/// <summary>WITH CHECK refuses a note whose resulting title is <see cref="Forbidden"/>.</summary>
public class EvNoteActions(IEntityMapper mapper) : DefaultPersistentObjectActions<EvNote>(mapper), ISparkOwnsRowSecurity
{
    public const string Forbidden = "forbidden";

    public string RowSecurityRationale => "Test fixture: the row rule is the refusal under test.";

    public override Task<bool> IsAllowedAsync(string action, EvNote entity)
        => Task.FromResult(entity.Title != Forbidden);
}

/// <summary>
/// Stores a side document in every before-hook — what Contributions does with a contribution — and
/// refuses the delete of a <see cref="Locked"/> note after storing it.
/// </summary>
public sealed class EvSideWritingInterceptor(IAsyncDocumentSession session) : IBeforeSave, IBeforeDelete
{
    public const string Locked = "locked";

    public bool AppliesTo(Type entityType) => entityType == typeof(EvNote);

    public async ValueTask OnBeforeSaveAsync(SaveContext context)
    {
        var note = (EvNote)context.Entity;
        await session.StoreAsync(new EvSide { Note = context.PersistentObject.Id ?? "(new)" }, $"EvSides/saved/{note.Title}");
    }

    public async ValueTask OnBeforeDeleteAsync(DeleteContext context)
    {
        var note = (EvNote)context.Entity;
        await session.StoreAsync(new EvSide { Note = context.Id }, $"EvSides/deleted/{note.Title}");
        if (note.Title == Locked)
            throw new InvalidOperationException("The note is locked.");
    }
}
