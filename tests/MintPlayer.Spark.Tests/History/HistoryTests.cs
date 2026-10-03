using MintPlayer.Spark.Tests._Infrastructure;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SoftDelete;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Revisions;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.History;

/// <summary>
/// #460 item 3 — the History package through the real route table: revisions configured from the
/// model, audit stamping, revision reads gated on the current row and redacted, revert through the
/// save pipeline (rights, row rule, disabled-action hook, soft deletion), observers.
/// </summary>
/// <remarks>
/// Fixture names start with <c>Hi</c>: <c>ActionsResolver</c> matches actions classes by simple
/// name across the whole assembly, and these types must not meet another fixture's.
/// </remarks>
public class HistoryTests(ITestOutputHelper output) : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("46030000-0000-4000-8000-000000000001");
    private static readonly Guid LineTypeId = Guid.Parse("46030000-0000-4000-8000-000000000002");
    private const string Alice = "users/alice";
    private const string Bob = "users/bob";

    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<Host> StartAsync(string? userId = Alice, SparkTestSecurity? security = null, Action<SparkHistoryOptions>? history = null)
    {
        var factory = new SparkEndpointFactory<HiContext>(
            Store,
            [NoteModel(), LineModel()],
            configureServices: services =>
            {
                services.AddSingleton<HiRecorder>();
                services.AddTestAfterCommitOutbox();
                services.AddScoped<HiNoteActions>();
                services.AddScoped<ISparkCurrentUser>(_ => new HiUser(userId));
            },
            configureSpark: spark =>
            {
                spark.AddSoftDelete();
                spark.AddHistory(history);
                spark.AddHook<HiObserver>();
                spark.AddHistoryUserNameResolver<HiNames>();
            },
            security: security ?? SparkTestSecurity.Permissive);
        factories.Add(factory);
        var (cookie, xsrf) = await factory.MintAntiforgeryAsync();
        return new Host(factory, factory.CreateClient(), cookie, xsrf);
    }

    // ---- T10: revisions configured from the model -------------------------------------------------

    [Fact]
    public async Task Startup_merges_the_models_revisions_into_the_database_and_keeps_everything_else()
    {
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Default = new RevisionsCollectionConfiguration { Disabled = true, MinimumRevisionsToKeep = 1 },
            Collections = new() { ["HiOthers"] = new() { Disabled = false, MinimumRevisionsToKeep = 2 } },
        }));

        var host = await StartAsync();

        var config = await ReadRevisionsConfigAsync();
        config.Collections[NotesCollection].Disabled.Should().BeFalse("the model enables revisions for HiNote");
        config.Collections["HiOthers"].MinimumRevisionsToKeep.Should().Be(2, "a collection no model type configures is kept");
        config.Default!.MinimumRevisionsToKeep.Should().Be(1, "the default is kept");

        // Idempotent: the same model again changes nothing, and sends nothing.
        var changed = await RevisionsConfigurator.ApplyAsync(Store, host.Factory.GetService<IModelLoader>(), host.Factory.GetService<IOptions<SparkHistoryOptions>>().Value, NullLogger.Instance);
        changed.Should().BeEmpty();
    }

    [Fact]
    public async Task Startup_merges_the_configured_revision_limits_and_keeps_every_other_collection()
    {
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Default = new RevisionsCollectionConfiguration { Disabled = true, MinimumRevisionsToKeep = 1 },
            Collections = new() { ["HiOthers"] = new() { Disabled = false, MinimumRevisionsToKeep = 2, MinimumRevisionAgeToKeep = TimeSpan.FromDays(3) } },
        }));

        var host = await StartAsync(history: o =>
        {
            o.Revisions.PurgeOnDelete = true;
            o.Types["hinote"] = new SparkRevisionTypeOptions { MinimumRevisionsToKeep = 2 };
        });

        var notes = (await ReadRevisionsConfigAsync()).Collections[NotesCollection];
        notes.Disabled.Should().BeFalse();
        notes.MinimumRevisionsToKeep.Should().Be(2, "the per-type limit (case-insensitive type name)");
        notes.MinimumRevisionAgeToKeep.Should().Be(SparkHistoryOptions.DefaultMinimumRevisionAgeToKeep, "the model sets no age, so the default applies");
        notes.PurgeOnDelete.Should().BeTrue("the model's purgeOnDelete false defers to the default");
        var config = await ReadRevisionsConfigAsync();
        config.Collections["HiOthers"].MinimumRevisionsToKeep.Should().Be(2, "a collection no model type configures is kept");
        config.Collections["HiOthers"].MinimumRevisionAgeToKeep.Should().Be(TimeSpan.FromDays(3));
        config.Default!.MinimumRevisionsToKeep.Should().Be(1, "the default is kept");

        (await RevisionsConfigurator.ApplyAsync(Store, host.Factory.GetService<IModelLoader>(), host.Factory.GetService<IOptions<SparkHistoryOptions>>().Value, NullLogger.Instance))
            .Should().BeEmpty("the same options again send nothing");
    }

    // ---- stamping ----------------------------------------------------------------------------------

    [Fact]
    public async Task Writes_are_stamped_with_user_ids_and_CreatedBy_cannot_be_changed()
    {
        var alice = await StartAsync(Alice);
        var bob = await StartAsync(Bob);

        var (created, body) = await alice.SendAsync("/spark/po/create", CreateBody(("Title", "first"), ("CreatedBy", "users/mallory")));
        created.Should().Be(HttpStatusCode.Created);
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        (await bob.SendAsync("/spark/po/update", UpdateBody(id, ("Title", "second"), ("CreatedBy", "users/mallory")))).Status.Should().Be(HttpStatusCode.OK);

        var stored = await LoadAsync<HiNote>(id);
        stored!.CreatedBy.Should().Be(Alice, "the creator is stamped, whatever was posted");
        stored.ModifiedBy.Should().Be(Bob);
        stored.CreatedAt.HasValue.Should().BeTrue();
        stored.ModifiedAt!.Value.Should().BeOnOrAfter(stored.CreatedAt!.Value);
    }

    // ---- H2 + reads --------------------------------------------------------------------------------

    [Fact]
    public async Task H2_the_etag_a_save_returns_is_the_newest_revisions_change_vector()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("v1");

        var (status, body) = await host.SendAsync("/spark/po/update", UpdateBody(note.Id!, ("Title", "v2")));

        status.Should().Be(HttpStatusCode.OK);
        var etag = body.GetProperty("result").GetProperty("etag").GetString();
        using var session = Store.OpenAsyncSession();
        var newest = (await session.Advanced.Revisions.GetMetadataForAsync(note.Id!, 0, 1))[0]["@change-vector"]?.ToString();
        output.WriteLine($"etag={etag} newestRevision={newest}");
        etag.Should().Be(newest);
    }

    [Fact]
    public async Task Revisions_are_listed_newest_first_with_who_wrote_them()
    {
        var alice = await StartAsync(Alice);
        var bob = await StartAsync(Bob);
        var (_, created) = await alice.SendAsync("/spark/po/create", CreateBody(("Title", "one")));
        var id = created.GetProperty("result").GetProperty("id").GetString()!;
        await bob.SendAsync("/spark/po/update", UpdateBody(id, ("Title", "two")));

        var (status, body) = await alice.SendAsync("/spark/po/revisions", Wire.Typed(NoteTypeId, id: id));

        status.Should().Be(HttpStatusCode.OK);
        var revisions = body.GetProperty("result").EnumerateArray().ToList();
        revisions.Should().HaveCount(2);
        revisions[0].GetProperty("userId").GetString().Should().Be(Bob);
        revisions[0].GetProperty("userName").GetString().Should().Be("Bob", "names are resolved at read time");
        revisions[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();
        revisions[1].GetProperty("userId").GetString().Should().Be(Alice);
        revisions[1].GetProperty("isCurrent").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_revision_is_redacted_when_the_attribute_is_protected_on_the_revision_or_on_the_current_row()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("open", secret: "s1");
        var openCv = await CurrentChangeVectorAsync(note.Id!);
        await UpdateRawAsync(note.Id!, n => { n.Title = "classified"; n.Secret = "s2"; });
        var classifiedCv = await CurrentChangeVectorAsync(note.Id!);
        await UpdateRawAsync(note.Id!, n => n.Title = "open again");

        var (_, oldOpen) = await host.SendAsync("/spark/po/revision", Wire.Typed(NoteTypeId, new { changeVector = openCv }, note.Id));
        var (_, oldClassified) = await host.SendAsync("/spark/po/revision", Wire.Typed(NoteTypeId, new { changeVector = classifiedCv }, note.Id));
        await UpdateRawAsync(note.Id!, n => n.Title = "classified");
        var (_, openWhileClassifiedNow) = await host.SendAsync("/spark/po/revision", Wire.Typed(NoteTypeId, new { changeVector = openCv }, note.Id));

        AttributeValue(oldOpen, "Secret").Should().Be("s1");
        AttributeValue(oldClassified, "Secret").Should().BeNull("protected on the revision");
        AttributeValue(openWhileClassifiedNow, "Secret").Should().BeNull("protected on the current row");
        AttributeValue(oldOpen, "Title").Should().Be("open");
    }

    [Fact]
    public async Task History_is_refused_without_the_right_for_a_hidden_row_and_for_another_rows_change_vector()
    {
        var host = await StartAsync();
        var denied = await StartAsync(security: SparkTestSecurity.Permissive.Denying("History/HiNote"));
        var note = await SeedNoteAsync("mine");
        var hidden = await SeedNoteAsync("hidden", owner: "hidden");
        var other = await SeedNoteAsync("other");
        var otherCv = await CurrentChangeVectorAsync(other.Id!);

        (await host.SendAsync("/spark/po/revisions", Wire.Typed(NoteTypeId, id: note.Id))).Status.Should().Be(HttpStatusCode.OK);
        (await denied.SendAsync("/spark/po/revisions", Wire.Typed(NoteTypeId, id: note.Id))).Status.Should().Be(HttpStatusCode.NotFound);
        (await host.SendAsync("/spark/po/revisions", Wire.Typed(NoteTypeId, id: hidden.Id))).Status.Should().Be(HttpStatusCode.NotFound, "a row the caller cannot load now has no history for them");
        (await host.SendAsync("/spark/po/revision", Wire.Typed(NoteTypeId, new { changeVector = otherCv }, note.Id))).Status.Should().Be(HttpStatusCode.NotFound, "a change vector is not scoped to a document");
        (await host.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = otherCv }, note.Id))).Status.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync<HiNote>(note.Id!))!.Title.Should().Be("mine");
    }

    // ---- revert --------------------------------------------------------------------------------------

    [Fact]
    public async Task H3_revert_restores_every_model_attribute_through_the_save_pipeline()
    {
        var host = await StartAsync(Bob);
        var original = new HiNote
        {
            Title = "v1",
            Label = new TranslatedString { Translations = { ["en"] = "one" } },
            DueAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(2)),
            Lines = [new HiLine { Text = "a", Quantity = 1 }, new HiLine { Text = "b", Quantity = 2 }],
            CreatedBy = Alice,
        };
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(original);
            await session.SaveChangesAsync();
        }
        var v1 = await CurrentChangeVectorAsync(original.Id!);
        await UpdateRawAsync(original.Id!, n =>
        {
            n.Title = "v2";
            n.Label = new TranslatedString { Translations = { ["en"] = "two", ["nl"] = "twee" } };
            n.DueAt = new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.FromHours(-5));
            n.Lines = [new HiLine { Text = "b", Quantity = 5 }, new HiLine { Text = "c", Quantity = 3 }];
        });
        await PatchUndeclaredFieldAsync(original.Id!, "added after v1");

        // Without the History package's fix: the base save merges TranslatedString per language.
        await SaveRevisionContentAsPlainEditAsync(host, original.Id!, v1);
        var plainEdit = await LoadAsync<HiNote>(original.Id!);
        output.WriteLine($"plain edit of v1's values: Label={JsonSerializer.Serialize(plainEdit!.Label?.Translations)}");
        await UpdateRawAsync(original.Id!, n => n.Label = new TranslatedString { Translations = { ["en"] = "two", ["nl"] = "twee" } });

        var (status, body) = await host.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = v1 }, original.Id));

        status.Should().Be(HttpStatusCode.OK);
        var reverted = await LoadAsync<HiNote>(original.Id!);
        var undeclared = await LoadAsync<HiNoteWithLegacy>(original.Id!);
        output.WriteLine($"reverted: Title={reverted!.Title} Label={JsonSerializer.Serialize(reverted.Label?.Translations)} DueAt={reverted.DueAt:O} Lines={JsonSerializer.Serialize(reverted.Lines)} Legacy={undeclared!.Legacy} CreatedBy={reverted.CreatedBy} ModifiedBy={reverted.ModifiedBy}");

        plainEdit.Label!.Translations.Should().ContainKey("nl", "the base save merges languages — a plain edit cannot remove one");
        reverted.Title.Should().Be("v1");
        reverted.Label!.Translations.Keys.Should().Equal(["en"], "a revert makes a TranslatedString exact");
        reverted.Label.Translations["en"].Should().Be("one");
        reverted.DueAt!.Value.Should().Be(original.DueAt!.Value);
        reverted.DueAt!.Value.Offset.Should().Be(TimeSpan.FromHours(2), "the offset survives");
        reverted.Lines.Select(l => (l.Text, l.Quantity)).Should().Equal(("a", 1), ("b", 2));
        undeclared.Legacy.Should().Be("added after v1", "a field the model does not declare is not reverted");
        reverted.CreatedBy.Should().Be(Alice, "CreatedBy is immutable");
        reverted.ModifiedBy.Should().Be(Bob, "a revert is a write by its caller");
        body.GetProperty("result").GetProperty("id").GetString().Should().Be(original.Id);
    }

    [Fact]
    public async Task Revert_needs_Revert_and_Edit_and_honours_the_disabled_action_hook()
    {
        var host = await StartAsync();
        var noRevert = await StartAsync(security: SparkTestSecurity.Permissive.Denying("Revert/HiNote"));
        var noEdit = await StartAsync(security: SparkTestSecurity.Permissive.Denying("Edit/HiNote"));
        var note = await SeedNoteAsync("v1");
        var v1 = await CurrentChangeVectorAsync(note.Id!);
        await UpdateRawAsync(note.Id!, n => n.Title = "frozen");

        (await noRevert.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = v1 }, note.Id))).Status.Should().Be(HttpStatusCode.NotFound);
        (await noEdit.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = v1 }, note.Id))).Status.Should().Be(HttpStatusCode.NotFound);
        var (frozen, body) = await host.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = v1 }, note.Id));

        frozen.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("result").GetProperty("action").GetString().Should().Be("Edit", "a hook withholding Edit refuses a revert");
        (await LoadAsync<HiNote>(note.Id!))!.Title.Should().Be("frozen");
    }

    /// <summary>
    /// W4 (contributions M2c-2b): an attribute the caller may not edit is not reverted, the rest is,
    /// and the response says the revert was partial — a warning in the envelope's operations. A revert
    /// that the denial does not affect says nothing.
    /// </summary>
    [Fact]
    public async Task A_revert_restores_only_what_the_caller_may_edit_and_reports_it_was_partial()
    {
        var host = await StartAsync(security: SparkTestSecurity.Permissive.Granting("Edit/HiNote").Denying("Edit/HiNote/Secret"));
        var note = await SeedNoteAsync("v1", secret: "s1");
        var v1 = await CurrentChangeVectorAsync(note.Id!);
        await UpdateRawAsync(note.Id!, n => { n.Title = "v2"; n.Secret = "s2"; });
        var v2 = await CurrentChangeVectorAsync(note.Id!);

        var (status, body) = await host.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = v1 }, note.Id));

        status.Should().Be(HttpStatusCode.OK);
        var reverted = await LoadAsync<HiNote>(note.Id!);
        reverted!.Title.Should().Be("v1", "Title may be edited, so it reverts");
        reverted.Secret.Should().Be("s2", "Secret may not be edited, so it keeps its current value");
        Notifications(body).Should().ContainSingle(m => m.Contains("partially"));
        // Every language travels, so ng-spark shows the one its user picked, not the browser's.
        var notice = body.GetProperty("operations").EnumerateArray().Single(o => o.GetProperty("type").GetString() == "notify");
        notice.TryGetProperty("translatedMessage", out var translations).Should().BeTrue("the client resolves the notice in the app-chosen language");
        translations.GetProperty("en").GetString().Should().Contain("partially");
        translations.GetProperty("nl").GetString().Should().StartWith("Gedeeltelijk teruggezet");

        // Back to v2's Title: Secret is s2 in both, so nothing was held back.
        await UpdateRawAsync(note.Id!, n => n.Title = "v3");
        var (again, quiet) = await host.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = v2 }, note.Id));

        again.Should().Be(HttpStatusCode.OK);
        (await LoadAsync<HiNote>(note.Id!))!.Title.Should().Be("v2");
        Notifications(quiet).Should().BeEmpty("the denied attribute already held the revision's value");
    }

    private static IEnumerable<string> Notifications(JsonElement envelope)
        => envelope.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array
            ? operations.EnumerateArray()
                .Where(o => o.TryGetProperty("type", out var type) && type.GetString() == "notify")
                .Select(o => o.GetProperty("message").GetString() ?? "")
                .ToList()
            : [];

    [Fact]
    public async Task Revert_is_judged_by_the_Actions_classs_Edit_rule()
    {
        // HiNoteActions filters "Edit" only (Owner != "other"). The row was the caller's in v1 and is
        // someone else's now: the revert would hand it back, and WITH CHECK (judging the RESULT, owner
        // null again) would not object. Only the row gate on the CURRENT row can refuse it — and it
        // does because a revert reaches the Actions class's hook as "Edit".
        var host = await StartAsync();
        var note = await SeedNoteAsync("v1");
        var v1 = await CurrentChangeVectorAsync(note.Id!);
        await UpdateRawAsync(note.Id!, n => { n.Title = "v2"; n.Owner = "other"; });

        var (status, _) = await host.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = v1 }, note.Id));

        status.Should().Be(HttpStatusCode.NotFound, "an edit rule that only names Edit still governs a revert");
        var stored = await LoadAsync<HiNote>(note.Id!);
        stored!.Title.Should().Be("v2");
        stored.Owner.Should().Be("other");
    }

    [Fact]
    public async Task A_revert_never_undeletes_and_a_deleted_row_cannot_be_reverted()
    {
        var host = await StartAsync();
        var note = await SeedNoteAsync("v1");
        (await host.SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: note.Id))).Status.Should().Be(HttpStatusCode.NoContent);
        var deletedCv = await CurrentChangeVectorAsync(note.Id!);
        (await LoadAsync<HiNote>(note.Id!))!.IsDeleted.Should().BeTrue();

        (await host.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = deletedCv }, note.Id))).Status
            .Should().Be(HttpStatusCode.NotFound, "a deleted row is hidden from the revert's row gate");

        (await host.SendAsync("/spark/po/restore", Wire.Typed(NoteTypeId, id: note.Id))).Status.Should().Be(HttpStatusCode.OK);
        var (status, _) = await host.SendAsync("/spark/po/revert", Wire.Typed(NoteTypeId, new { changeVector = deletedCv }, note.Id));

        status.Should().Be(HttpStatusCode.OK);
        var stored = await LoadAsync<HiNote>(note.Id!);
        stored!.IsDeleted.Should().BeFalse("SoftDelete keeps the stored soft-delete fields on a revert");
        stored.DeletedAt.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task A_deleted_rows_history_is_readable_with_the_deleted_flag_by_ViewDeleted_holders_only()
    {
        // M7 finding: History had no load-side deleted flag, so a soft-deleted row's history was a 404
        // for everyone. Same flag and gate as /spark/po/load (M7 carry-over).
        var host = await StartAsync();
        var denied = await StartAsync(security: SparkTestSecurity.Permissive.Denying("ViewDeleted/HiNote"));
        var note = await SeedNoteAsync("v1");
        var v1 = await CurrentChangeVectorAsync(note.Id!);
        (await host.SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: note.Id))).Status.Should().Be(HttpStatusCode.NoContent);

        var (plain, _) = await host.SendAsync("/spark/po/revisions", Wire.Typed(NoteTypeId, id: note.Id));
        var (included, list) = await host.SendAsync("/spark/po/revisions", Wire.Typed(NoteTypeId, new { deleted = "include" }, note.Id));
        var (only, _) = await host.SendAsync("/spark/po/revisions", Wire.Typed(NoteTypeId, new { deleted = "only" }, note.Id));
        var (revision, content) = await host.SendAsync("/spark/po/revision", Wire.Typed(NoteTypeId, new { changeVector = v1, deleted = "include" }, note.Id));
        var (withoutRight, _) = await denied.SendAsync("/spark/po/revisions", Wire.Typed(NoteTypeId, new { deleted = "include" }, note.Id));
        var (revisionWithoutRight, _) = await denied.SendAsync("/spark/po/revision", Wire.Typed(NoteTypeId, new { changeVector = v1, deleted = "only" }, note.Id));

        plain.Should().Be(HttpStatusCode.NotFound, "without the flag a deleted row stays hidden");
        included.Should().Be(HttpStatusCode.OK);
        list.GetProperty("result").GetArrayLength().Should().BeGreaterThan(1);
        only.Should().Be(HttpStatusCode.OK);
        revision.Should().Be(HttpStatusCode.OK);
        AttributeValue(content, "Title").Should().Be("v1");
        withoutRight.Should().Be(HttpStatusCode.NotFound, "the flag is honoured only for ViewDeleted holders");
        revisionWithoutRight.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- durable after-commit hooks (formerly revision observers; #482, D32(4)) -------------------------

    [Fact]
    public async Task Durable_hooks_hear_every_write_with_its_kind_changed_attributes_and_previous_change_vector()
    {
        var host = await StartAsync();
        var (_, created) = await host.SendAsync("/spark/po/create", CreateBody(("Title", "one")));
        var id = created.GetProperty("result").GetProperty("id").GetString()!;
        var createdCv = created.GetProperty("result").GetProperty("etag").GetString();
        var (_, updated) = await host.SendAsync("/spark/po/update", UpdateBody(id, ("Title", "two")));
        var updatedCv = updated.GetProperty("result").GetProperty("etag").GetString();
        await host.SendAsync("/spark/po/delete", Wire.Typed(NoteTypeId, id: id));
        await host.SendAsync("/spark/po/restore", Wire.Typed(NoteTypeId, id: id));

        host.Recorder.Events.Should().BeEmpty("nothing runs before the outbox delivers");
        await host.DrainAsync();

        var events = host.Recorder.Events.ToList();
        output.WriteLine(string.Join(Environment.NewLine, events.Select(e => $"{e.Operation} replaced={e.IsReplaced} facts=[{string.Join(";", e.Facts.Select(f => $"{f.Key}={f.Value}"))}] prev={e.PreviousChangeVector} user={e.UserId}")));

        events.Select(e => e.Operation).Should().Equal(
            PersistentObjectOperation.New, PersistentObjectOperation.Save, PersistentObjectOperation.Delete, PersistentObjectOperation.Restore);
        events.Should().OnlyContain(e => e.Id == id && e.UserId == Alice && e.EntityType == typeof(HiNote).FullName);
        events[0].PreviousChangeVector.Should().BeNull();
        events[1].Facts[SparkFacts.ChangedAttributes].Split(',').Should().Contain("Title");
        events[1].PreviousChangeVector.Should().Be(createdCv);
        events[2].IsReplaced.Should().BeTrue("SoftDelete replaced the delete");
        events[2].PreviousChangeVector.Should().Be(updatedCv);
        events[3].PreviousChangeVector.Should().NotBeNull().And.NotBe(updatedCv, "the restore found the soft-deleted version");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private string NotesCollection => Store.Conventions.FindCollectionName(typeof(HiNote));

    private async Task<RevisionsConfiguration> ReadRevisionsConfigAsync()
        => (await Store.Maintenance.SendAsync(new RevisionsConfigurator.GetRevisionsConfigurationOperation()))!;

    private async Task<HiNote> SeedNoteAsync(string title, string? owner = null, string? secret = null)
    {
        var note = new HiNote { Title = title, Owner = owner, Secret = secret };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(note);
        await session.SaveChangesAsync();
        return note;
    }

    private async Task UpdateRawAsync(string id, Action<HiNote> change)
    {
        using var session = Store.OpenAsyncSession();
        var note = await session.LoadAsync<HiNote>(id);
        change(note);
        await session.SaveChangesAsync();
    }

    private async Task PatchUndeclaredFieldAsync(string id, string value)
    {
        await Store.Operations.SendAsync(new Raven.Client.Documents.Operations.PatchOperation(id, null, new Raven.Client.Documents.Operations.PatchRequest
        {
            Script = "this.Legacy = args.value;",
            Values = { ["value"] = value },
        }));
    }

    /// <summary>The base pipeline alone: v1's values saved as an ordinary edit (what a revert would be without the package's fix).</summary>
    private async Task SaveRevisionContentAsPlainEditAsync(Host host, string id, string changeVector)
    {
        using var scope = host.Factory.CreateScope();
        var mapper = scope.ServiceProvider.GetRequiredService<IEntityMapper>();
        var databaseAccess = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();
        using var session = Store.OpenAsyncSession();
        var revision = await session.Advanced.Revisions.GetAsync<HiNote>(changeVector);
        var po = mapper.ToPersistentObject(revision, NoteTypeId);
        po.Id = id;
        foreach (var attribute in po.Attributes)
            attribute.IsValueChanged = true;
        await databaseAccess.SavePersistentObjectAsync(po);
    }

    private async Task<string> CurrentChangeVectorAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        var note = await session.LoadAsync<HiNote>(id);
        return session.Advanced.GetChangeVectorFor(note);
    }

    private async Task<T?> LoadAsync<T>(string id) where T : class
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }

    private static string? AttributeValue(JsonElement envelope, string name)
    {
        var attribute = envelope.GetProperty("result").GetProperty("attributes").EnumerateArray().First(a => a.GetProperty("name").GetString() == name);
        return attribute.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static object CreateBody(params (string Name, object Value)[] attributes) => Wire.Typed(NoteTypeId, new
    {
        persistentObject = new
        {
            name = "HiNote",
            objectTypeId = NoteTypeId.ToString(),
            attributes = attributes.Select(a => new { name = a.Name, value = a.Value, isValueChanged = true }).ToArray(),
        },
    });

    private static object UpdateBody(string id, params (string Name, object Value)[] attributes) => Wire.Typed(NoteTypeId, new
    {
        persistentObject = new
        {
            id,
            name = "HiNote",
            objectTypeId = NoteTypeId.ToString(),
            attributes = attributes.Select(a => new { name = a.Name, value = a.Value, isValueChanged = true }).ToArray(),
        },
    }, id);

    private static EntityTypeFile NoteModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "HiNote",
            ClrType = typeof(HiNote).FullName!,
            Breadcrumb = "{Title}",
            Revisions = new EntityRevisionsDefinition { Enabled = true },
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "Secret", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "Owner", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "Label", DataType = "TranslatedString", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "DueAt", DataType = "datetime", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail", AsDetailType = typeof(HiLine).FullName, IsArray = true, IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "CreatedBy", DataType = "string", IsVisible = true },
            ],
        },
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "HiLine",
            ClrType = typeof(HiLine).FullName!,
            Breadcrumb = "{Text}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Text", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "Quantity", DataType = "number", IsVisible = true },
            ],
        },
    };

    private sealed record Host(SparkEndpointFactory<HiContext> Factory, HttpClient Client, string Cookie, string Xsrf)
    {
        public HiRecorder Recorder => Factory.GetService<HiRecorder>();

        /// <summary>Runs the durable after-commit hooks of every committed write so far (#482, D17).</summary>
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
    }
}

public class HiNote : IAuditable, ISoftDeletable
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Secret { get; set; }
    public string? Owner { get; set; }
    public TranslatedString? Label { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public List<HiLine> Lines { get; set; } = [];
    public string? CreatedBy { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

/// <summary>The same document read with a field the model never declared.</summary>
public class HiNoteWithLegacy
{
    public string? Id { get; set; }
    public string? Legacy { get; set; }
}

public class HiLine
{
    public string Text { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed class HiRecorder
{
    public ConcurrentQueue<SparkCommittedChange> Events { get; } = new();
}

/// <summary>What replaced <c>ISparkRevisionObserver</c> (#482, D32(4)): durable after-commit hooks.</summary>
public sealed class HiObserver(HiRecorder recorder) : IAfterSaveCommitted<HiNote>, IAfterDeleteCommitted<HiNote>
{
    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        recorder.Events.Enqueue(change);
        return Task.CompletedTask;
    }

    public Task OnAfterDeleteCommittedAsync(SparkCommittedChange change, CancellationToken cancellationToken)
    {
        recorder.Events.Enqueue(change);
        return Task.CompletedTask;
    }
}

public sealed class HiNames : IHistoryUserNameResolver
{
    public Task<IReadOnlyDictionary<string, string>> ResolveAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(userIds
            .Where(id => id is "users/alice" or "users/bob")
            .ToDictionary(id => id, id => id == "users/alice" ? "Alice" : "Bob"));
}

public sealed class HiUser(string? id) : ISparkCurrentUser
{
    public string? Id => id;
    public bool IsAuthenticated => id is not null;
}

/// <summary>
/// Hides rows owned by "hidden" from reads; lets only non-"other" rows be edited (the rule names Edit
/// only); protects Secret on a row titled "classified"; withholds Edit on a row titled "frozen".
/// </summary>
public class HiNoteActions(IEntityMapper mapper) : DefaultPersistentObjectActions<HiNote>(mapper)
{
    public override Task<Expression<Func<HiNote, bool>>?> GetRowFilterAsync(string action) => Task.FromResult<Expression<Func<HiNote, bool>>?>(action switch
    {
        "Read" or "Query" => x => x.Owner != "hidden",
        "Edit" => x => x.Owner != "other",
        _ => null,
    });

    public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, HiNote entity)
        => Task.FromResult<IReadOnlyCollection<string>?>(entity.Title == "classified" ? ["Secret"] : null);

    public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
    {
        if (context.Entity is HiNote { Title: "frozen" })
            target.DisableActions("Edit");
        return Task.CompletedTask;
    }
}

public class HiContext : SparkContext
{
    public Raven.Client.Documents.Linq.IRavenQueryable<HiNote> Notes => Session.Query<HiNote>();
}
