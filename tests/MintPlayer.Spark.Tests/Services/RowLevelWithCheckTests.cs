using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// M2 (#236) — the write half of row-level security: SQL RLS's <c>WITH CHECK</c> to the read
/// paths' <c>USING</c>.
/// <para>
/// Before this, <c>SavePersistentObjectAsync</c> skipped the row gate for id-less saves and
/// checked edits only against the <b>pre</b>-update state — nothing stopped an authenticated
/// caller creating a document stamped with someone else's owner, or editing a row <em>into</em>
/// someone else's scope. The rule is now judged against the entity's resulting state, after
/// mapping and <c>OnBeforeSaveAsync</c> (so ownership stamping has happened). The system context
/// (module sync, background work) is exempt — row rules scope viewers, and infrastructure has
/// none (D3).
/// </para>
/// </summary>
public class RowLevelWithCheckTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("d8d8d8d8-8888-8888-8888-d8d8d8d8d8d8");

    public class Note
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
    }

    /// <summary>Rows belong to alice; nothing is stamped automatically.</summary>
    public class ScopedNoteActions : DefaultPersistentObjectActions<Note>
    {
        public ScopedNoteActions(IEntityMapper entityMapper, IHttpContextAccessor? accessor = null)
            : base(entityMapper, accessor) { }

        public override System.Linq.Expressions.Expression<Func<Note, bool>>? GetRowFilter(string action)
            => n => n.Owner == "alice";
    }

    /// <summary>The Fleet pattern: the app stamps ownership in OnBeforeSaveAsync, and the
    /// WITH CHECK judges the stamped result.</summary>
    public class StampingNoteActions : DefaultPersistentObjectActions<Note>
    {
        public StampingNoteActions(IEntityMapper entityMapper) : base(entityMapper) { }

        public override System.Linq.Expressions.Expression<Func<Note, bool>>? GetRowFilter(string action)
            => n => n.Owner == "alice";

        public override Task OnBeforeSaveAsync(PersistentObject obj, Note entity)
        {
            if (string.IsNullOrEmpty(entity.Owner))
                entity.Owner = "alice";
            return Task.CompletedTask;
        }
    }

    private static IModelLoader CreateModelLoader()
    {
        var noteDef = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "Note",
            ClrType = typeof(Note).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Owner", DataType = "string", Order = 2 },
            ],
            Breadcrumb = "{Title}",
        };
        var modelLoader = Substitute.For<IModelLoader>();
        modelLoader.GetEntityType(NoteTypeId).Returns(noteDef);
        modelLoader.GetEntityTypeByClrType(typeof(Note).FullName!).Returns(noteDef);
        return modelLoader;
    }

    private static PersistentObject NotePo(string? id, string title, string? owner)
    {
        var po = new PersistentObject { Id = id, ObjectTypeId = NoteTypeId, Name = "Note" };
        po.AddAttribute(new PersistentObjectAttribute
        {
            Name = "Title", DataType = "string", Value = title, IsValueChanged = true,
        });
        po.AddAttribute(new PersistentObjectAttribute
        {
            Name = "Owner", DataType = "string", Value = owner, IsValueChanged = owner is not null,
        });
        return po;
    }

    private static IHttpContextAccessor SystemContextAccessor()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(SparkSystemContext.ClaimType, "module")], "test")),
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(context);
        return accessor;
    }

    [Fact]
    public async Task Creating_a_row_outside_the_callers_scope_is_refused()
    {
        var actions = new ScopedNoteActions(new EntityMapper(CreateModelLoader()));
        using var session = Store.OpenAsyncSession();

        var act = () => actions.OnSaveAsync(session, NotePo(null, "Bob's note", "bob"));

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "a create must produce a row its caller could see — WITH CHECK, not just USING");
    }

    [Fact]
    public async Task A_create_that_stamps_ownership_first_passes_the_check()
    {
        var actions = new StampingNoteActions(new EntityMapper(CreateModelLoader()));
        using var session = Store.OpenAsyncSession();

        var saved = await actions.OnSaveAsync(session, NotePo(null, "Mine", owner: null));

        saved.Owner.Should().Be("alice",
            "the check runs after OnBeforeSaveAsync, so the stamped result is what gets judged");
        saved.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Editing_a_row_into_someone_elses_scope_is_refused()
    {
        string noteId;
        using (var session = Store.OpenAsyncSession())
        {
            var note = new Note { Title = "Mine", Owner = "alice" };
            await session.StoreAsync(note);
            await session.SaveChangesAsync();
            noteId = note.Id!;
        }

        var actions = new ScopedNoteActions(new EntityMapper(CreateModelLoader()));
        using var editSession = Store.OpenAsyncSession();

        var act = () => actions.OnSaveAsync(editSession, NotePo(noteId, "Mine", "bob"));

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "the pre-update state passed the read gate, but the post-update state leaves the "
            + "caller's scope — that write hands the row to someone else");
    }

    [Fact]
    public async Task The_system_context_is_exempt_from_the_write_check()
    {
        var actions = new ScopedNoteActions(new EntityMapper(CreateModelLoader()), SystemContextAccessor());
        using var session = Store.OpenAsyncSession();

        var saved = await actions.OnSaveAsync(session, NotePo(null, "Synced from HR", "bob"));

        saved.Owner.Should().Be("bob",
            "module sync writes documents on behalf of other modules' users — a viewer-scoped "
            + "rule must not refuse infrastructure (D3)");
    }

    [Fact]
    public async Task A_context_with_no_system_claim_is_not_exempt_even_without_an_http_request()
    {
        // Fail closed: the absence of an HTTP request is the DEFAULT state of every non-request
        // code path, so it must not switch row security off. Only a positive system claim exempts.
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var actions = new ScopedNoteActions(new EntityMapper(CreateModelLoader()), accessor);
        using var session = Store.OpenAsyncSession();

        var act = () => actions.OnSaveAsync(session, NotePo(null, "From an unproven caller", "bob"));

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "no system claim means the caller is treated as a viewer and the row rule applies");
    }

    [Fact]
    public async Task The_system_context_is_exempt_from_the_read_gates_too()
    {
        var modelLoader = CreateModelLoader();
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Note))
            .Returns(new ScopedNoteActions(new EntityMapper(modelLoader)));
        var rowSecurity = new RowSecurity(actionsResolver, null, SystemContextAccessor());

        (await rowSecurity.IsAllowedAsync(typeof(Note), "Read", new Note { Owner = "bob" }))
            .Should().BeTrue();

        using var session = Store.OpenAsyncSession();
        var rows = new List<object> { new Note { Owner = "bob" }, new Note { Owner = "carol" } };
        (await rowSecurity.FilterAsync(session, rows, typeof(Note), typeof(Note), "Query"))
            .Should().HaveCount(2, "sync reads back what it wrote regardless of row scoping");
    }
}
