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
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
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
                s.AddSoftDeleteObserver<SdObserver>();
                services?.Invoke(s);
            },
            configureSpark: spark =>
            {
                spark.AddSoftDelete();
                // Registered after SoftDelete, so it refuses AFTER the soft-delete mark was set.
                spark.Services.AddPersistentObjectInterceptor<SdVetoInterceptor>();
            },
            security: security ?? SparkTestSecurity.Permissive);
        factories.Add(factory);
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        return new Host(factory, factory.CreateClient(), cookie, xsrf);
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
        (await RevisionCountAsync(note.Id!)).Should().BeGreaterThan(0);
        host.Recorder.Events.Clear();

        var (status, _) = await host.SendAsync("/spark/po/purge", Wire.Typed(NoteTypeId, id: note.Id));

        status.Should().Be(HttpStatusCode.NoContent);
        (await LoadAsync<SdNote>(note.Id!)).Should().BeNull();
        (await RevisionCountAsync(note.Id!)).Should().Be(0);
        host.Recorder.OnDeleteCalls.Should().Equal(note.Id!);
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
        Queries = [new SparkQuery { Id = NotesQueryId, Name = "SdNotes", Source = "Database.Notes", EntityType = "SdNote" }],
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
}

/// <summary>Withholds Edit on a row titled "frozen" and Delete on one titled "keep"; records OnDeleteAsync.</summary>
public class SdNoteActions(IEntityMapper mapper, SdRecorder recorder) : DefaultPersistentObjectActions<SdNote>(mapper)
{
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

    public override Task OnDeleteAsync(IAsyncDocumentSession session, string id)
    {
        recorder.OnDeleteCalls.Enqueue(id);
        return base.OnDeleteAsync(session, id);
    }
}

/// <summary>Refuses the delete of a row titled "veto" — registered after SoftDelete, so the mark is already set.</summary>
public sealed class SdVetoInterceptor : IPersistentObjectInterceptor
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

public sealed class SdObserver(SdRecorder recorder) : ISoftDeleteObserver
{
    public ValueTask OnDeletedAsync(SoftDeleteEvent e) { recorder.Events.Enqueue($"Deleted:{e.Id}"); return ValueTask.CompletedTask; }
    public ValueTask OnRestoredAsync(SoftDeleteEvent e) { recorder.Events.Enqueue($"Restored:{e.Id}"); return ValueTask.CompletedTask; }
    public ValueTask OnPurgedAsync(SoftDeleteEvent e) { recorder.Events.Enqueue($"Purged:{e.Id}"); return ValueTask.CompletedTask; }
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
