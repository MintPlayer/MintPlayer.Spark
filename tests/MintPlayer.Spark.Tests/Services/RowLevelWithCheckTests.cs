using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using Microsoft.Extensions.DependencyInjection;
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
/// mapping and the before-save hooks (so ownership stamping has happened), by the framework write (#482). The system context
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

        public override Task<System.Linq.Expressions.Expression<Func<Note, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<System.Linq.Expressions.Expression<Func<Note, bool>>?>(n => n.Owner == "alice");
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
        await using var host = Host();
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var act = () => db.SavePersistentObjectAsync(WcPo(null, "Bob's note", "bob"));

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "a create must produce a row its caller could see — WITH CHECK, not just USING");
    }

    [Fact]
    public async Task A_create_that_stamps_ownership_first_passes_the_check()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var saved = await db.SavePersistentObjectAsync(WcPo(null, "Mine", owner: null));

        saved.Id.Should().NotBeNullOrEmpty();
        using var verify = Store.OpenAsyncSession();
        (await verify.LoadAsync<WcNote>(saved.Id)).Owner.Should().Be("alice",
            "the check runs after the before-save hooks, so the stamped result is what gets judged");
    }

    [Fact]
    public async Task Editing_a_row_into_someone_elses_scope_is_refused()
    {
        string noteId;
        using (var session = Store.OpenAsyncSession())
        {
            var note = new WcNote { Title = "Mine", Owner = "alice" };
            await session.StoreAsync(note);
            await session.SaveChangesAsync();
            noteId = note.Id!;
        }

        await using var host = Host();
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var act = () => db.SavePersistentObjectAsync(WcPo(noteId, "Mine", "bob"));

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "the pre-update state passed the read gate, but the post-update state leaves the "
            + "caller's scope — that write hands the row to someone else");
    }

    [Fact]
    public async Task The_system_context_is_exempt_from_the_write_check()
    {
        await using var host = Host();
        using var scope = host.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = SystemContextAccessor().HttpContext;
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var saved = await db.SavePersistentObjectAsync(WcPo(null, "Synced from HR", "bob"));

        using var verify = Store.OpenAsyncSession();
        (await verify.LoadAsync<WcNote>(saved.Id)).Owner.Should().Be("bob",
            "module sync writes documents on behalf of other modules' users — a viewer-scoped "
            + "rule must not refuse infrastructure (D3)");
    }

    [Fact]
    public async Task A_context_with_no_system_claim_is_not_exempt_even_without_an_http_request()
    {
        // Fail closed: the absence of an HTTP request is the DEFAULT state of every non-request
        // code path, so it must not switch row security off. Only a positive system claim exempts.
        await using var host = Host();
        using var scope = host.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        var db = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var act = () => db.SavePersistentObjectAsync(WcPo(null, "From an unproven caller", "bob"));

        await act.Should().ThrowAsync<SparkRowLevelAccessDeniedException>(
            "no system claim means the caller is treated as a viewer and the row rule applies");
    }

    private static readonly Guid WcNoteTypeId = Guid.Parse("d8d8d8d8-8888-8888-8888-d8d8d8d8d8d9");

    /// <summary>A host whose WcNote rows belong to alice, stamped by the type's own before-save hook.</summary>
    private SparkEndpointFactory<WcContext> Host() => new(Store, [new EntityTypeFile
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = WcNoteTypeId,
            Name = "WcNote",
            ClrType = typeof(WcNote).FullName!,
            Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string", Order = 1 },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Owner", DataType = "string", Order = 2 },
            ],
        },
    }]);

    private static PersistentObject WcPo(string? id, string title, string? owner)
    {
        var po = NotePo(id, title, owner);
        po.ObjectTypeId = WcNoteTypeId;
        po.Name = "WcNote";
        return po;
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

/// <summary>A row of <see cref="RowLevelWithCheckTests"/>' host.</summary>
public class WcNote
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
}

/// <summary>Rows belong to alice; the type's own hook stamps her on a create that names no owner (the Fleet pattern).</summary>
public class WcNoteActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<WcNote>(entityMapper), IBeforeSave<WcNote>
{
    public override Task<System.Linq.Expressions.Expression<Func<WcNote, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<System.Linq.Expressions.Expression<Func<WcNote, bool>>?>(n => n.Owner == "alice");

    public ValueTask OnBeforeSaveAsync(WcNote entity, SaveContext context)
    {
        if (string.IsNullOrEmpty(entity.Owner))
            entity.Owner = "alice";
        return ValueTask.CompletedTask;
    }
}

public class WcContext : SparkContext
{
    public Raven.Client.Documents.Linq.IRavenQueryable<WcNote> Notes => Session.Query<WcNote>();
}
