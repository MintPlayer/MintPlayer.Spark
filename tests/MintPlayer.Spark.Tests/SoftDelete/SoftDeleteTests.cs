using MintPlayer.Spark.Tests._Infrastructure;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.SoftDelete;

/// <summary>
/// #460 item 2 — the SoftDelete package through the real route table: a delete is soft, deleted rows
/// are hidden everywhere unless a <c>ViewDeleted</c> holder asks, restore and purge are gated under
/// their own names (right, row gate, disabled-action hook), a purge takes the revisions with it, and
/// a save cannot point a reference at a deleted row.
/// </summary>
/// <remarks>
/// Fixture names start with <c>Sd</c>: <c>ActionsResolver</c> matches actions classes by simple
/// name across the whole assembly, and these types must not meet another fixture's.
/// </remarks>
public class SoftDeleteTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("46020000-0000-4000-8000-000000000001");
    private static readonly Guid PersonTypeId = Guid.Parse("46020000-0000-4000-8000-000000000002");
    private static readonly Guid SlugTypeId = Guid.Parse("46020000-0000-4000-8000-000000000003");
    private static readonly Guid NotesQueryId = Guid.Parse("46020000-0000-4000-8000-000000000011");
    private static readonly Guid NotesByAuthorQueryId = Guid.Parse("46020000-0000-4000-8000-000000000012");

    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<Host> StartAsync(SparkTestSecurity? security = null, Action<IServiceCollection>? services = null)
    {
        var factory = new SparkEndpointFactory<SdContext>(
            Store,
            [NoteModel(), PersonModel(), SlugModel()],
            configureServices: s =>
            {
                s.AddSingleton<SdRecorder>();
                s.AddScoped<SdNoteActions>();
                s.AddTestAfterCommitOutbox();
                s.AddSparkInterceptor<SdObserver>();
                services?.Invoke(s);
            },
            configureSpark: spark =>
            {
                spark.AddSoftDelete();
                // Registered after SoftDelete, so it refuses AFTER the soft-delete mark was set.
                spark.Services.AddSparkInterceptor<SdVetoInterceptor>();
            },
            security: security ?? SparkTestSecurity.Permissive);
        factories.Add(factory);
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        return new Host(factory, factory.CreateClient(), cookie, xsrf);
    }

    // ---- the raw-delete guard (#467, D32) --------------------------------------------------------

    [Fact]
    public async Task A_raw_session_delete_of_a_soft_deletable_row_is_refused()
    {
        await StartAsync();
        var note = await SeedNoteAsync("raw");

        using (var session = Store.OpenAsyncSession())
        {
            session.Delete(note.Id!);
            var act = () => session.SaveChangesAsync();
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*soft-deletable*");
        }

        (await LoadAsync<SdNote>(note.Id!)).Should().NotBeNull("the raw delete bypassed SoftDelete and was refused");
    }

    [Fact]
    public async Task A_raw_delete_inside_SparkRawWrites_Allow_goes_through()
    {
        await StartAsync();
        var note = await SeedNoteAsync("migrated");

        using (var session = Store.OpenAsyncSession())
        using (SparkRawWrites.Allow())
        {
            session.Delete(note.Id!);
            await session.SaveChangesAsync();
        }

        (await LoadAsync<SdNote>(note.Id!)).Should().BeNull("the opt-out is for migrations and fixtures");
    }

    [Fact]
    public async Task A_purge_through_the_framework_is_not_refused_by_the_guard()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("purged", deleted: true);

        using (var scope = host.Factory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
            await db.DeletePersistentObjectAsync(NoteTypeId, note.Id!, PersistentObjectOperation.Purge);
        }

        (await LoadAsync<SdNote>(note.Id!)).Should().BeNull("the persister issued this hard delete");
    }

    // ---- delete is soft ------------------------------------------------------------------------

    [Fact]
    public async Task A_delete_keeps_the_document_marked_deleted_and_skips_the_OnDeleteAsync_override()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("plain");

        var (status, _) = await host.SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: note.Id));

        status.Should().Be(HttpStatusCode.NoContent);
        var stored = await LoadAsync<SdNote>(note.Id!);
        stored.Should().NotBeNull("a soft delete keeps the document");
        stored!.IsDeleted.Should().BeTrue();
        stored.DeletedAt.HasValue.Should().BeTrue();
        host.Recorder.OnDeleteCalls.Should().BeEmpty("the replacement is decided before the Actions class is asked");
        await host.DrainAsync();
        host.Recorder.Events.Should().Equal($"Deleted:{note.Id}");
    }

    [Fact]
    public async Task A_deleted_row_is_not_loadable_and_not_listed()
    {
        var host = await StartAsync();
        var live = await SeedNoteAsync("live");
        var gone = await SeedNoteAsync("gone", deleted: true);

        var (loadStatus, _) = await host.SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, id: gone.Id));
        var ids = await host.QueryIdsAsync(null);

        loadStatus.Should().Be(HttpStatusCode.NotFound);
        ids.Should().Equal(live.Id!);
    }

    [Fact]
    public async Task A_document_stored_before_the_field_existed_counts_as_live()
    {
        var host = await StartAsync();
        using (var session = Store.OpenAsyncSession())
        {
            // Stored as a type without IsDeleted, into the notes collection: the field is absent.
            var legacy = new SdLegacyNote { Id = "SdNotes/legacy", Title = "legacy" };
            await session.StoreAsync(legacy);
            session.Advanced.GetMetadataFor(legacy)["@collection"] = Store.Conventions.FindCollectionName(typeof(SdNote));
            session.Advanced.GetMetadataFor(legacy)["Raven-Clr-Type"] = typeof(SdNote).AssemblyQualifiedName;
            await session.SaveChangesAsync();
        }

        var ids = await host.QueryIdsAsync(null);

        ids.Should().Contain("SdNotes/legacy", "'IsDeleted != true' matches an absent field (spike S3)");
    }

    [Fact]
    public async Task ViewDeleted_holders_can_ask_for_deleted_rows_and_others_are_ignored()
    {
        var host = await StartAsync();
        var live = await SeedNoteAsync("live");
        var gone = await SeedNoteAsync("gone", deleted: true);

        (await host.QueryIdsAsync("include")).Should().BeEquivalentTo([live.Id!, gone.Id!]);
        (await host.QueryIdsAsync("only")).Should().Equal(gone.Id!);

        var denied = await StartAsync(SparkTestSecurity.Permissive.Denying("ViewDeleted/SdNote"));
        (await denied.QueryIdsAsync("include")).Should().Equal(live.Id!);
        // Without ViewDeleted the flag is ignored, not an error.
        (await denied.QueryIdsAsync("only")).Should().Equal(live.Id!);
    }

    [Fact]
    public async Task An_edit_cannot_change_the_soft_delete_fields()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("live");

        var (status, _) = await host.SendAsync("/spark/po/update", UpdateBody(note.Id!, ("IsDeleted", true), ("Title", "renamed")));

        status.Should().Be(HttpStatusCode.OK);
        var stored = await LoadAsync<SdNote>(note.Id!);
        stored!.Title.Should().Be("renamed");
        stored.IsDeleted.Should().BeFalse("the fields are the framework's");
    }

    [Fact]
    public async Task DeleteAsync_records_the_reason()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("plain");

        using (var scope = host.Factory.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISparkSoftDelete>().DeleteAsync(NoteTypeId, note.Id!, "spam");

        (await LoadAsync<SdNote>(note.Id!))!.DeleteReason.Should().Be("spam");

        // The durable interceptors see the reason too (#482, D34b): SoftDelete records it as a fact, since the
        // framework only knows a reason the caller passed to a bulk delete.
        await host.DrainAsync();
        host.Recorder.Reasons.Should().Equal("spam");
    }

    // ---- restore ---------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_brings_a_deleted_row_back()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("gone", deleted: true);

        var (status, body) = await host.SendAsync("/spark/po/restore", Wire.Typed(NoteTypeId, id: note.Id));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("result").GetProperty("id").GetString().Should().Be(note.Id);
        var stored = await LoadAsync<SdNote>(note.Id!);
        stored!.IsDeleted.Should().BeFalse();
        stored.DeletedAt.HasValue.Should().BeFalse();
        stored.DeleteReason.Should().BeNull();
        stored.Title.Should().Be("gone", "a restore changes nothing but the soft-delete fields");
        await host.DrainAsync();
        host.Recorder.Events.Should().Equal($"Restored:{note.Id}");
    }

    [Fact]
    public async Task Restore_refuses_a_live_row_a_missing_row_and_a_caller_without_the_right()
    {
        var host = await StartAsync();
        var live = await SeedNoteAsync("live");
        var gone = await SeedNoteAsync("gone", deleted: true);
        var denied = await StartAsync(SparkTestSecurity.Permissive.Denying("Restore/SdNote"));

        (await host.SendAsync("/spark/po/restore", Wire.Typed(NoteTypeId, id: live.Id))).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.SendAsync("/spark/po/restore", Wire.Typed(NoteTypeId, id: "SdNotes/missing"))).Status.Should().Be(HttpStatusCode.NotFound);
        (await denied.SendAsync("/spark/po/restore", Wire.Typed(NoteTypeId, id: gone.Id))).Status.Should().Be(HttpStatusCode.NotFound);

        (await LoadAsync<SdNote>(gone.Id!))!.IsDeleted.Should().BeTrue();
        (await LoadAsync<SdNote>("SdNotes/missing")).Should().BeNull("a restore never creates");
    }

    [Fact]
    public async Task Restore_is_refused_when_the_hook_withholds_Edit()
    {
        var host = await StartAsync();
        var frozen = await SeedNoteAsync("frozen", deleted: true);

        var (status, body) = await host.SendAsync("/spark/po/restore", Wire.Typed(NoteTypeId, id: frozen.Id));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("result").GetProperty("action").GetString().Should().Be("Edit", "the refusal names the withheld action that refused it");
        (await LoadAsync<SdNote>(frozen.Id!))!.IsDeleted.Should().BeTrue();
    }

    // ---- purge -----------------------------------------------------------------------------------

    [Fact]
    public async Task Purge_removes_a_deleted_row_and_every_revision_of_it()
    {
        var host = await StartAsync();
        await EnableRevisionsAsync();
        var note = await SeedNoteAsync("gone");
        await host.SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: note.Id));
        await host.DrainAsync();
        (await RevisionCountAsync(note.Id!)).Should().BeGreaterThan(0);
        host.Recorder.Events.Clear();

        var (status, _) = await host.SendAsync("/spark/po/purge", Wire.Typed(NoteTypeId, id: note.Id));

        status.Should().Be(HttpStatusCode.NoContent);
        (await LoadAsync<SdNote>(note.Id!)).Should().BeNull();
        (await RevisionCountAsync(note.Id!)).Should().Be(0);
        host.Recorder.OnDeleteCalls.Should().Equal(note.Id!);
        await host.DrainAsync();
        host.Recorder.Events.Should().Equal($"Purged:{note.Id}");
    }

    [Fact]
    public async Task Purge_refuses_a_live_row_a_foreign_id_and_a_caller_without_the_right_and_keeps_their_revisions()
    {
        var host = await StartAsync();
        await EnableRevisionsAsync();
        var live = await SeedNoteAsync("live");
        var person = await SeedPersonAsync("someone");
        var gone = await SeedNoteAsync("gone", deleted: true);
        var denied = await StartAsync(SparkTestSecurity.Permissive.Denying("Purge/SdNote"));
        var liveRevisions = await RevisionCountAsync(live.Id!);

        (await host.SendAsync("/spark/po/purge", Wire.Typed(NoteTypeId, id: live.Id))).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.SendAsync("/spark/po/purge", Wire.Typed(NoteTypeId, id: person.Id))).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.SendAsync("/spark/po/purge", Wire.Typed(NoteTypeId, id: "SdNotes/missing"))).Status.Should().Be(HttpStatusCode.NotFound);
        (await denied.SendAsync("/spark/po/purge", Wire.Typed(NoteTypeId, id: gone.Id))).Status.Should().Be(HttpStatusCode.NotFound);

        (await LoadAsync<SdNote>(live.Id!)).Should().NotBeNull();
        (await LoadAsync<SdPerson>(person.Id!)).Should().NotBeNull("a foreign-collection id is not found, not purged");
        (await LoadAsync<SdNote>(gone.Id!)).Should().NotBeNull();
        (await RevisionCountAsync(live.Id!)).Should().Be(liveRevisions);
        host.Recorder.OnDeleteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Purge_is_refused_when_the_hook_withholds_Delete()
    {
        var host = await StartAsync();
        var kept = await SeedNoteAsync("keep", deleted: true);

        var (status, body) = await host.SendAsync("/spark/po/purge", Wire.Typed(NoteTypeId, id: kept.Id));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("result").GetProperty("action").GetString().Should().Be("Delete", "the refusal names the withheld action that refused it");
        (await LoadAsync<SdNote>(kept.Id!)).Should().NotBeNull();
    }

    // ---- references and natural ids --------------------------------------------------------------

    [Fact]
    public async Task A_save_may_not_point_a_reference_at_a_deleted_row_without_ViewDeleted_on_its_type()
    {
        var deletedAuthor = await SeedPersonAsync("gone", deleted: true);
        var liveAuthor = await SeedPersonAsync("here");
        var note = await SeedNoteAsync("plain");
        var host = await StartAsync(SparkTestSecurity.Permissive.Denying("ViewDeleted/SdPerson"));
        var viewer = await StartAsync();

        var (refused, body) = await host.SendAsync("/spark/po/update", UpdateBody(note.Id!, ("AuthorId", deletedAuthor.Id!)));
        var (allowedLive, _) = await host.SendAsync("/spark/po/update", UpdateBody(note.Id!, ("AuthorId", liveAuthor.Id!)));
        var (allowedHolder, _) = await viewer.SendAsync("/spark/po/update", UpdateBody(note.Id!, ("AuthorId", deletedAuthor.Id!)));

        refused.Should().Be(HttpStatusCode.BadRequest);
        body.GetRawText().Should().Contain("refers to a deleted SdPerson");
        allowedLive.Should().Be(HttpStatusCode.OK);
        allowedHolder.Should().Be(HttpStatusCode.OK, "a ViewDeleted holder on the target type may");
    }

    [Fact]
    public async Task An_existing_reference_to_a_row_deleted_since_does_not_block_other_edits()
    {
        var author = await SeedPersonAsync("author");
        var note = await SeedNoteAsync("plain", authorId: author.Id);
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<SdPerson>(author.Id!)).IsDeleted = true;
            await session.SaveChangesAsync();
        }
        var host = await StartAsync(SparkTestSecurity.Permissive.Denying("ViewDeleted/SdPerson"));

        var (status, _) = await host.SendAsync("/spark/po/update", UpdateBody(note.Id!, ("Title", "renamed")));

        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Creating_a_row_whose_natural_id_a_deleted_row_holds_says_restore_instead()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new SdSlug { Name = "alpha", IsDeleted = true }, "SdSlugs/alpha");
            await session.SaveChangesAsync();
        }
        var holder = await StartAsync();
        var stranger = await StartAsync(SparkTestSecurity.Permissive.Denying("ViewDeleted/SdSlug", "Restore/SdSlug"));
        var create = Wire.Typed(SlugTypeId, new
        {
            persistentObject = new
            {
                name = "SdSlug",
                objectTypeId = SlugTypeId.ToString(),
                attributes = new[] { new { name = "Name", value = "alpha", isValueChanged = true } },
            },
        });

        var (holderStatus, holderBody) = await holder.SendAsync("/spark/po/create", create);
        var (strangerStatus, _) = await stranger.SendAsync("/spark/po/create", create);

        holderStatus.Should().Be(HttpStatusCode.BadRequest);
        holderBody.GetRawText().Should().Contain("Restore it instead");
        strangerStatus.Should().Be(HttpStatusCode.NotFound, "a caller who may not see deleted rows is told nothing new");
        (await LoadAsync<SdSlug>("SdSlugs/alpha"))!.IsDeleted.Should().BeTrue();
    }

    // ---- M7 carry-overs ----------------------------------------------------------------------------

    [Fact]
    public async Task A_delete_a_later_interceptor_refuses_leaves_no_mark_for_a_later_save_in_the_request()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("veto");

        using (var scope = host.Factory.CreateScope())
        {
            Exception? refused = null;
            try { await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().DeletePersistentObjectAsync(NoteTypeId, note.Id!); }
            catch (Exception ex) { refused = ex; }
            refused.Should().BeOfType<SparkValidationException>();

            // Any later write in the same request commits the request session.
            await scope.ServiceProvider.GetRequiredService<IAsyncDocumentSession>().SaveChangesAsync();
        }

        var stored = await LoadAsync<SdNote>(note.Id!);
        stored!.IsDeleted.Should().BeFalse("a refused delete must leave nothing behind");
        stored.DeletedAt.HasValue.Should().BeFalse();
        await host.DrainAsync();
        host.Recorder.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_ViewDeleted_holder_opens_a_deleted_row_with_the_load_deleted_flag()
    {
        var host = await StartAsync();
        var denied = await StartAsync(SparkTestSecurity.Permissive.Denying("ViewDeleted/SdNote"));
        var live = await SeedNoteAsync("live");
        var gone = await SeedNoteAsync("gone", deleted: true);

        var (included, body) = await host.SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, new { deleted = "include" }, gone.Id));
        var (onlyLive, _) = await host.SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, new { deleted = "only" }, live.Id));
        var (liveIncluded, _) = await host.SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, new { deleted = "include" }, live.Id));
        var (withoutRight, _) = await denied.SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, new { deleted = "include" }, gone.Id));
        var (withoutFlag, _) = await host.SendAsync("/spark/po/load", Wire.Typed(NoteTypeId, id: gone.Id));

        included.Should().Be(HttpStatusCode.OK);
        body.GetProperty("id").GetString().Should().Be(gone.Id);
        onlyLive.Should().Be(HttpStatusCode.NotFound, "'only' opens deleted rows only");
        liveIncluded.Should().Be(HttpStatusCode.OK);
        withoutRight.Should().Be(HttpStatusCode.NotFound, "the flag is ignored without ViewDeleted");
        withoutFlag.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_permissions_endpoint_reports_the_soft_delete_rights()
    {
        var host = await StartAsync();
        var denied = await StartAsync(SparkTestSecurity.Permissive.Denying("Restore/SdNote", "Purge/SdNote", "ViewDeleted/SdNote"));

        var granted = await host.GetAsync("/spark/permissions/SdNote");
        var refused = await denied.GetAsync("/spark/permissions/SdNote");

        granted.GetProperty("canRestore").GetBoolean().Should().BeTrue();
        granted.GetProperty("canPurge").GetBoolean().Should().BeTrue();
        granted.GetProperty("canViewDeleted").GetBoolean().Should().BeTrue();
        refused.GetProperty("canRestore").GetBoolean().Should().BeFalse();
        refused.GetProperty("canPurge").GetBoolean().Should().BeFalse();
        refused.GetProperty("canViewDeleted").GetBoolean().Should().BeFalse();
        refused.GetProperty("canDelete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_purge_the_revisions_probe_refuses_deletes_nothing()
    {
        var host = await StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton<ISoftDeleteRevisions>(new SdRefusingRevisions())));
        await EnableRevisionsAsync();
        var note = await SeedNoteAsync("gone");
        await host.SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: note.Id));
        var revisions = await RevisionCountAsync(note.Id!);

        Exception? refused = null;
        using (var scope = host.Factory.CreateScope())
        {
            try { await scope.ServiceProvider.GetRequiredService<ISparkSoftDelete>().PurgeAsync(NoteTypeId, note.Id!); }
            catch (Exception ex) { refused = ex; }
        }

        refused.Should().BeOfType<InvalidOperationException>();
        (await LoadAsync<SdNote>(note.Id!)).Should().NotBeNull("the probe runs before the document is deleted");
        (await RevisionCountAsync(note.Id!)).Should().Be(revisions);
        host.Recorder.OnDeleteCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_and_purge_are_judged_by_the_Actions_classs_Edit_and_Delete_rules()
    {
        // SdNoteActions filters "Edit" and "Delete" only. Restore reaches that hook as Edit, purge as
        // Delete. (Measured without the mapping: the purge went through — 204; the restore was still
        // refused, by the WITH CHECK that judges the restored row under "Edit".)
        var host = await StartAsync();
        var theirs = await SeedNoteAsync("not-mine", deleted: true);

        (await host.SendAsync("/spark/po/restore", Wire.Typed(NoteTypeId, id: theirs.Id))).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.SendAsync("/spark/po/purge", Wire.Typed(NoteTypeId, id: theirs.Id))).Status.Should().Be(HttpStatusCode.NotFound);

        var stored = await LoadAsync<SdNote>(theirs.Id!);
        stored.Should().NotBeNull();
        stored!.IsDeleted.Should().BeTrue();
    }

    // ---- nothing but Restore and Purge reaches a deleted row -------------------------------------

    /// <summary>
    /// The recycle bin offers only Restore and Purge (#460). A hand-made request that aims the default
    /// Delete or a custom action at a deleted row is the same 404 as a hidden row — both judge live
    /// rows only — and a <c>deleted</c> field smuggled into the body widens nothing. The live control
    /// proves the refusal is about the row, not the request.
    /// </summary>
    [Fact]
    public async Task The_default_Delete_and_a_custom_action_on_a_deleted_row_are_404()
    {
        var host = await StartAsync(services: s =>
        {
            s.AddScoped<SdTouchAction>();
            s.AddSingleton(TestActions.LoaderWithCustom(SdTouchAction.Name));
            s.AddScoped<ICustomActionResolver>(sp => new SdActionResolver(SdTouchAction.Name, sp.GetRequiredService<SdTouchAction>()));
        });
        var live = await SeedNoteAsync("live");
        var gone = await SeedNoteAsync("gone", deleted: true);

        object ActionOn(string id, bool smuggleDeleted) => smuggleDeleted
            ? Wire.Action(NoteTypeId, SdTouchAction.Name, new { selectedItemIds = new[] { id }, queryId = NotesQueryId.ToString(), deleted = "only" })
            : Wire.Action(NoteTypeId, SdTouchAction.Name, new { selectedItemIds = new[] { id }, queryId = NotesQueryId.ToString() });
        object DeleteMany(string id, bool smuggleDeleted) => smuggleDeleted
            ? Wire.Typed(NoteTypeId, new { ids = new[] { id }, queryId = NotesQueryId.ToString(), deleted = "only" })
            : Wire.Typed(NoteTypeId, new { ids = new[] { id }, queryId = NotesQueryId.ToString() });

        var (actionStatus, _) = await host.SendAsync("/spark/actions/execute", ActionOn(gone.Id!, smuggleDeleted: false));
        var (smuggledActionStatus, _) = await host.SendAsync("/spark/actions/execute", ActionOn(gone.Id!, smuggleDeleted: true));
        var (deleteStatus, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany(gone.Id!, smuggleDeleted: false));
        var (smuggledDeleteStatus, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany(gone.Id!, smuggleDeleted: true));

        actionStatus.Should().Be(HttpStatusCode.NotFound);
        smuggledActionStatus.Should().Be(HttpStatusCode.NotFound);
        deleteStatus.Should().Be(HttpStatusCode.NotFound);
        smuggledDeleteStatus.Should().Be(HttpStatusCode.NotFound);
        await host.DrainAsync();
        host.Recorder.Events.Should().BeEmpty("neither the action nor a second delete ran on the deleted row");
        var stored = await LoadAsync<SdNote>(gone.Id!);
        stored!.IsDeleted.Should().BeTrue();
        stored.DeletedAt.HasValue.Should().BeFalse("a second soft delete would have stamped it");

        // Control: the same requests on a live row go through.
        var (liveAction, _) = await host.SendAsync("/spark/actions/execute", ActionOn(live.Id!, smuggleDeleted: false));
        var (liveDelete, _) = await host.SendAsync("/spark/po/delete-many", DeleteMany(live.Id!, smuggleDeleted: false));

        liveAction.Should().Be(HttpStatusCode.OK);
        liveDelete.Should().Be(HttpStatusCode.NoContent);
        await host.DrainAsync();
        host.Recorder.Events.Should().Equal($"Touched:{live.Id}", $"Deleted:{live.Id}");
    }

    // ---- a sub-query on a deleted parent (parentDeleted) ------------------------------------------

    [Fact]
    public async Task A_ViewDeleted_holder_lists_the_sub_query_of_a_deleted_parent_with_parentDeleted()
    {
        var host = await StartAsync();
        var (author, live, _) = await SeedDeletedAuthorAsync();

        var (status, body) = await host.SendAsync("/spark/queries/execute", SubQuery(author.Id!, parentDeleted: "include"));

        status.Should().Be(HttpStatusCode.OK);
        ItemIds(body).Should().Equal(live.Id!);
    }

    [Fact]
    public async Task ParentDeleted_leaves_the_rows_own_deleted_filter_alone()
    {
        var host = await StartAsync();
        var (author, live, gone) = await SeedDeletedAuthorAsync();

        var (includeStatus, include) = await host.SendAsync("/spark/queries/execute", SubQuery(author.Id!, parentDeleted: "include", deleted: "include"));
        var (onlyStatus, only) = await host.SendAsync("/spark/queries/execute", SubQuery(author.Id!, parentDeleted: "include", deleted: "only"));

        includeStatus.Should().Be(HttpStatusCode.OK);
        onlyStatus.Should().Be(HttpStatusCode.OK);
        ItemIds(include).Should().BeEquivalentTo([live.Id!, gone.Id!]);
        ItemIds(only).Should().Equal(gone.Id!);
    }

    [Fact]
    public async Task Without_parentDeleted_a_deleted_parent_is_still_a_404_even_when_the_rows_ask_for_deleted()
    {
        var host = await StartAsync();
        var (author, _, _) = await SeedDeletedAuthorAsync();

        var (plain, _) = await host.SendAsync("/spark/queries/execute", SubQuery(author.Id!));
        var (rowsOnly, _) = await host.SendAsync("/spark/queries/execute", SubQuery(author.Id!, deleted: "include"));
        var (distinct, _) = await host.SendAsync("/spark/queries/distinct-values", SubQueryDistinct(author.Id!));

        plain.Should().Be(HttpStatusCode.NotFound);
        rowsOnly.Should().Be(HttpStatusCode.NotFound, "`deleted` is the rows' filter, never the parent's");
        distinct.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_ViewDeleted_on_the_parent_type_a_deleted_parent_is_the_same_404_as_a_missing_one()
    {
        var host = await StartAsync(SparkTestSecurity.Permissive.Denying("ViewDeleted/SdPerson"));
        var (author, _, _) = await SeedDeletedAuthorAsync();

        var (deletedStatus, deletedBody) = await host.SendAsync("/spark/queries/execute", SubQuery(author.Id!, parentDeleted: "include"));
        var (missingStatus, missingBody) = await host.SendAsync("/spark/queries/execute", SubQuery("SdPeople/missing", parentDeleted: "include"));
        var (distinctDeleted, distinctDeletedBody) = await host.SendAsync("/spark/queries/distinct-values", SubQueryDistinct(author.Id!, parentDeleted: "include"));
        var (distinctMissing, distinctMissingBody) = await host.SendAsync("/spark/queries/distinct-values", SubQueryDistinct("SdPeople/missing", parentDeleted: "include"));

        deletedStatus.Should().Be(HttpStatusCode.NotFound);
        missingStatus.Should().Be(HttpStatusCode.NotFound);
        deletedBody.GetRawText().Should().Be(missingBody.GetRawText(), "missing and forbidden look the same (#453)");
        distinctDeleted.Should().Be(HttpStatusCode.NotFound);
        distinctMissing.Should().Be(HttpStatusCode.NotFound);
        distinctDeletedBody.GetRawText().Should().Be(distinctMissingBody.GetRawText());

        // Control: the right that is denied is the PARENT's, not the rows'.
        var holder = await StartAsync(SparkTestSecurity.Permissive.Denying("ViewDeleted/SdNote"));
        var (holderStatus, _) = await holder.SendAsync("/spark/queries/execute", SubQuery(author.Id!, parentDeleted: "include"));
        holderStatus.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_ViewDeleted_holder_gets_the_distinct_values_of_a_deleted_parents_sub_query()
    {
        var host = await StartAsync();
        var (author, _, _) = await SeedDeletedAuthorAsync();

        var (status, body) = await host.SendAsync("/spark/queries/distinct-values", SubQueryDistinct(author.Id!, parentDeleted: "include"));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("matching").EnumerateArray().Select(v => v.GetProperty("value").GetString()).Should().Equal("by the deleted author");
    }

    /// <summary>
    /// Actions and bulk delete resolve their sub-query parent as a LIVE row, always: the recycle bin
    /// offers no actions under a deleted object, so a smuggled <c>parentDeleted</c> widens nothing.
    /// </summary>
    [Fact]
    public async Task A_custom_action_and_delete_many_under_a_deleted_parent_are_refused_even_with_parentDeleted()
    {
        var host = await StartAsync(services: s =>
        {
            s.AddScoped<SdTouchAction>();
            s.AddSingleton(TestActions.LoaderWithCustom(SdTouchAction.Name));
            s.AddScoped<ICustomActionResolver>(sp => new SdActionResolver(SdTouchAction.Name, sp.GetRequiredService<SdTouchAction>()));
        });
        var (author, live, _) = await SeedDeletedAuthorAsync();

        object ActionUnder(string parentId) => Wire.Action(NoteTypeId, SdTouchAction.Name, new
        {
            selectedItemIds = new[] { live.Id },
            queryId = NotesByAuthorQueryId.ToString(),
            parentId,
            parentType = "SdPerson",
            parentDeleted = "include",
        });
        object DeleteManyUnder(string parentId) => Wire.Typed(NoteTypeId, new
        {
            ids = new[] { live.Id },
            queryId = NotesByAuthorQueryId.ToString(),
            parentId,
            parentType = "SdPerson",
            parentDeleted = "include",
        });

        var (actionStatus, _) = await host.SendAsync("/spark/actions/execute", ActionUnder(author.Id!));
        var (deleteStatus, _) = await host.SendAsync("/spark/po/delete-many", DeleteManyUnder(author.Id!));

        actionStatus.Should().Be(HttpStatusCode.NotFound);
        deleteStatus.Should().Be(HttpStatusCode.NotFound);
        await host.DrainAsync();
        host.Recorder.Events.Should().BeEmpty("neither ran under the deleted parent");
        (await LoadAsync<SdNote>(live.Id!))!.IsDeleted.Should().NotBe(true);

        // Control: the same requests under a live parent go through.
        var liveAuthor = await SeedPersonAsync("live author");
        var liveNote = await SeedNoteAsync("by the live author", authorId: liveAuthor.Id);
        var (liveAction, _) = await host.SendAsync("/spark/actions/execute", Wire.Action(NoteTypeId, SdTouchAction.Name, new
        {
            selectedItemIds = new[] { liveNote.Id },
            queryId = NotesByAuthorQueryId.ToString(),
            parentId = liveAuthor.Id,
            parentType = "SdPerson",
        }));
        liveAction.Should().Be(HttpStatusCode.OK);
        await host.DrainAsync();
        host.Recorder.Events.Should().Equal($"Touched:{liveNote.Id}");
    }

    // ---- startup ---------------------------------------------------------------------------------

    [Fact]
    public void A_soft_deletable_type_with_an_explicit_IsDeleted_refuses_startup()
    {
        var act = () => new SparkEndpointFactory<SdBrokenContext>(
            Store,
            [new EntityTypeFile { PersistentObject = new EntityTypeDefinition { Id = Guid.NewGuid(), Name = "SdBroken", ClrType = typeof(SdBroken).FullName!, Attributes = [] } }],
            configureSpark: spark => spark.AddSoftDelete());

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("SdBroken.IsDeleted is not a public property");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<SdNote> SeedNoteAsync(string title, bool deleted = false, string? authorId = null)
    {
        var note = new SdNote { Title = title, IsDeleted = deleted, AuthorId = authorId };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(note);
        await session.SaveChangesAsync();
        return note;
    }

    private async Task<SdPerson> SeedPersonAsync(string name, bool deleted = false)
    {
        var person = new SdPerson { Name = name, IsDeleted = deleted };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(person);
        await session.SaveChangesAsync();
        return person;
    }

    /// <summary>A deleted person with one live and one deleted note, plus a live note by someone else.</summary>
    private async Task<(SdPerson Author, SdNote Live, SdNote Gone)> SeedDeletedAuthorAsync()
    {
        var author = await SeedPersonAsync("deleted author", deleted: true);
        var other = await SeedPersonAsync("other author");
        var live = await SeedNoteAsync("by the deleted author", authorId: author.Id);
        var gone = await SeedNoteAsync("deleted, by the deleted author", deleted: true, authorId: author.Id);
        await SeedNoteAsync("by someone else", authorId: other.Id);
        return (author, live, gone);
    }

    private static System.Text.Json.Nodes.JsonNode SubQuery(string parentId, string? parentDeleted = null, string? deleted = null)
    {
        var body = Wire.Query(NotesByAuthorQueryId, new { parentId, parentType = "SdPerson" });
        if (parentDeleted is not null) body["parentDeleted"] = parentDeleted;
        if (deleted is not null) body["deleted"] = deleted;
        return body;
    }

    private static System.Text.Json.Nodes.JsonNode SubQueryDistinct(string parentId, string? parentDeleted = null)
    {
        var body = Wire.Query(NotesByAuthorQueryId, new { column = "Title", parentId, parentType = "SdPerson" });
        if (parentDeleted is not null) body["parentDeleted"] = parentDeleted;
        return body;
    }

    private static IReadOnlyList<string> ItemIds(JsonElement body)
        => body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).OrderBy(i => i).ToList();

    private async Task<T?> LoadAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    private Task EnableRevisionsAsync() => Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
    {
        Collections = new Dictionary<string, RevisionsCollectionConfiguration>
        {
            [Store.Conventions.FindCollectionName(typeof(SdNote))] = new() { Disabled = false },
            [Store.Conventions.FindCollectionName(typeof(SdPerson))] = new() { Disabled = false },
        },
    }));

    private async Task<long> RevisionCountAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Advanced.Revisions.GetCountForAsync(id);
    }

    private static object UpdateBody(string id, params (string Name, object Value)[] attributes) => Wire.Typed(NoteTypeId, new
    {
        persistentObject = new
        {
            id,
            name = "SdNote",
            objectTypeId = NoteTypeId.ToString(),
            attributes = attributes.Select(a => new { name = a.Name, value = a.Value, isValueChanged = true }).ToArray(),
        },
    }, id);

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "SdNote",
            ClrType = typeof(SdNote).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "AuthorId", DataType = "Reference", ReferenceType = typeof(SdPerson).FullName, IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "IsDeleted", DataType = "bool", IsVisible = true },
            ],
        },
        Queries =
        [
            new SparkQuery { Id = NotesQueryId, Name = "SdNotes", Source = "Database.Notes", EntityType = "SdNote" },
            new SparkQuery { Id = NotesByAuthorQueryId, Name = "SdNotesByAuthor", Source = "Custom.NotesByAuthor", EntityType = "SdNote" },
        ],
    };

    private static EntityTypeFile PersonModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "SdPerson",
            ClrType = typeof(SdPerson).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", IsVisible = true }],
        },
    };

    private static EntityTypeFile SlugModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SlugTypeId,
            Name = "SdSlug",
            ClrType = typeof(SdSlug).FullName!,
            Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", IsVisible = true }],
        },
    };

    private sealed record Host(SparkEndpointFactory<SdContext> Factory, HttpClient Client, string Cookie, string Xsrf)
    {
        public SdRecorder Recorder => Factory.GetService<SdRecorder>();

        /// <summary>Runs the durable after-commit interceptors of every committed write so far (#482, D17).</summary>
        public Task<int> DrainAsync() => Factory.GetService<TestAfterCommitOutbox>().DrainAsync(Factory.GetService<IServiceProvider>());

        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string url, object payload)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            request.Headers.Add("Cookie", Cookie);
            request.Headers.Add("X-XSRF-TOKEN", Xsrf);
            var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
        }

        public async Task<JsonElement> GetAsync(string url)
        {
            var response = await Client.GetAsync(url);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        public async Task<IReadOnlyList<string>> QueryIdsAsync(string? deleted)
        {
            var (status, body) = await SendAsync("/spark/queries/execute", Wire.Query(NotesQueryId, deleted is null ? null : new { deleted }));
            status.Should().Be(HttpStatusCode.OK);
            return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).OrderBy(i => i).ToList();
        }
    }
}

public class SdNote : ISoftDeletable
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    [Reference(typeof(SdPerson))] public string? AuthorId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

/// <summary>A note as stored before the type was soft-deletable: no <c>IsDeleted</c> at all.</summary>
public class SdLegacyNote
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class SdPerson : ISoftDeletable
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

public class SdSlug : ISoftDeletable, IHasNaturalId
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
    public string GetId() => "SdSlugs/" + Name;
}

public class SdBroken : ISoftDeletable
{
    public string? Id { get; set; }
    bool ISoftDeletable.IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

public sealed class SdRecorder
{
    public ConcurrentQueue<string> OnDeleteCalls { get; } = new();
    public ConcurrentQueue<string> Events { get; } = new();
    public ConcurrentQueue<string?> Reasons { get; } = new();
}

/// <summary>Withholds Edit on a row titled "frozen" and Delete on one titled "keep"; records OnDeleteAsync.</summary>
public class SdNoteActions(IEntityMapper mapper, SdRecorder recorder, IAsyncDocumentSession session) : DefaultPersistentObjectActions<SdNote>(mapper),
    IAfterDelete<SdNote>
{
    /// <summary>The sub-query on a person's page: the notes they wrote.</summary>
    public IRavenQueryable<SdNote> NotesByAuthor(MintPlayer.Spark.Queries.CustomQueryArgs args)
    {
        ArgumentNullException.ThrowIfNull(args.Parent);
        return session.Query<SdNote>().Where(n => n.AuthorId == args.Parent!.Id);
    }

    /// <summary>A rule written for the built-in verbs only: a row titled "not-mine" may not be edited or deleted.</summary>
    public override Task<System.Linq.Expressions.Expression<Func<SdNote, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<System.Linq.Expressions.Expression<Func<SdNote, bool>>?>(
            action is "Edit" or "Delete" ? x => x.Title != "not-mine" : null);

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is SdNote { Title: "frozen" })
            target.DisableActions("Edit");
        if (context.Entity is SdNote { Title: "keep" })
            target.DisableActions("Delete");
        return Task.CompletedTask;
    }

    /// <summary>Records every committed hard delete (a purge); a replaced (soft) delete is not one.</summary>
    public ValueTask OnAfterDeleteAsync(SdNote entity, DeleteContext context)
    {
        if (!context.IsReplaced)
            recorder.OnDeleteCalls.Enqueue(context.Id);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Refuses the delete of a row titled "veto" — after SoftDelete decided the replacement, so the mark is already set.</summary>
public sealed class SdVetoInterceptor : IBeforeDelete
{
    public bool AppliesTo(Type entityType) => entityType == typeof(SdNote);

    public ValueTask OnBeforeDeleteAsync(DeleteContext context)
        => context.Entity is SdNote { Title: "veto" } ? throw new SparkValidationException("Vetoed.") : ValueTask.CompletedTask;
}

/// <summary>A connection that may not delete revisions (a certificate without database-admin).</summary>
internal sealed class SdRefusingRevisions : ISoftDeleteRevisions
{
    public Task EnsureCanDeleteAsync() => throw new InvalidOperationException("Purge refused: the probe was not allowed.");

    public Task<long> DeleteAsync(string id) => throw new InvalidOperationException("Must not be reached: the probe refused.");
}

/// <summary>A custom action that records the rows it ran on.</summary>
public sealed class SdTouchAction(SdRecorder recorder) : ICustomAction
{
    public const string Name = "SdTouch";

    public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        foreach (var item in args.SelectedItems)
            recorder.Events.Enqueue($"Touched:{item.Id}");
        return Task.CompletedTask;
    }
}

internal sealed class SdActionResolver(string name, ICustomAction action) : ICustomActionResolver
{
    public ICustomAction? Resolve(string actionName)
        => string.Equals(actionName, name, StringComparison.OrdinalIgnoreCase) ? action : null;

    public IReadOnlyList<string> GetRegisteredActionNames() => [name];
}

/// <summary>What replaced <c>ISoftDeleteObserver</c> (#482, D32(4)): durable after-commit interceptors.</summary>
public sealed class SdObserver(SdRecorder recorder) : IAfterDeleteCommitted<SdNote>, IAfterSaveCommitted<SdNote>
{
    public Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        if (change.IsReplaced)
        {
            recorder.Events.Enqueue($"Deleted:{change.Id}");
            recorder.Reasons.Enqueue(change.Reason);
        }
        else if (change.IsPurge)
            recorder.Events.Enqueue($"Purged:{change.Id}");
        return Task.CompletedTask;
    }

    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        if (change.Operation == PersistentObjectOperation.Restore)
            recorder.Events.Enqueue($"Restored:{change.Id}");
        return Task.CompletedTask;
    }
}

public class SdContext : SparkContext
{
    public Raven.Client.Documents.Linq.IRavenQueryable<SdNote> Notes => Session.Query<SdNote>();
    public Raven.Client.Documents.Linq.IRavenQueryable<SdPerson> People => Session.Query<SdPerson>();
    public Raven.Client.Documents.Linq.IRavenQueryable<SdSlug> Slugs => Session.Query<SdSlug>();
}

public class SdBrokenContext : SparkContext
{
    public Raven.Client.Documents.Linq.IRavenQueryable<SdBroken> Broken => Session.Query<SdBroken>();
}
