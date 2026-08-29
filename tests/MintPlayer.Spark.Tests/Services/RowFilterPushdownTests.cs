using System.Linq.Expressions;
using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// M1 (#236) — <c>GetRowFilter</c>: the row rule as an expression the framework composes into the
/// RavenDB query, with single-row checks derived by compiling the same expression.
/// <para>
/// Derivation, not interaction: filter-only types get list and detail from one source of truth;
/// predicate-only types behave exactly as before; types with both must pass both. A null return
/// means "no restriction for this caller"; a constant-false means "nothing", evaluated in memory
/// because a constant predicate need not (and may not) survive query translation.
/// </para>
/// </summary>
public class RowFilterPushdownTests : SparkTestDriver
{
    public class Note
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
    }

    public class VNote
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
    }

    /// <summary>Filter-only: the expression is the whole policy.</summary>
    public class ScopedNoteActions : DefaultPersistentObjectActions<Note>
    {
        public static string? CurrentUser = "alice";
        public static bool IsAdmin;

        public ScopedNoteActions(IEntityMapper entityMapper) : base(entityMapper) { }

        public override async Task<Expression<Func<Note, bool>>?> GetRowFilterAsync(string action)
        {
            await Task.Yield();   // prove the async path: genuinely suspends before returning
            if (IsAdmin) return null;
            var user = CurrentUser;
            if (string.IsNullOrEmpty(user)) return n => false;
            return n => n.Owner == user;
        }
    }

    /// <summary>Both members: the filter narrows to the owner, the predicate refines per action.</summary>
    public class GuardedNoteActions : DefaultPersistentObjectActions<Note>
    {
        public GuardedNoteActions(IEntityMapper entityMapper) : base(entityMapper) { }

        public override Task<Expression<Func<Note, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<Expression<Func<Note, bool>>?>(n => n.Owner == "alice");

        public override Task<bool> IsAllowedAsync(string action, Note entity)
            => Task.FromResult(action != "Edit" || !entity.Title.Contains("locked"));
    }

    /// <summary>Counts how many times the async hook actually runs, to pin the M2 memo.</summary>
    public class CountingNoteActions : DefaultPersistentObjectActions<Note>
    {
        public int Invocations;
        public CountingNoteActions(IEntityMapper entityMapper) : base(entityMapper) { }

        public override Task<Expression<Func<Note, bool>>?> GetRowFilterAsync(string action)
        {
            Interlocked.Increment(ref Invocations);
            return Task.FromResult<Expression<Func<Note, bool>>?>(n => n.Owner == "alice");
        }
    }

    [Fact]
    public async Task The_hook_runs_once_per_type_action_per_request_independent_of_row_count()
    {
        // M2 (#239): an async hook that does I/O must run a bounded number of times — bounded by
        // the model (distinct type×action), never by rows/pages/batches — or the breadcrumb loop
        // alone blows RavenDB's 30-request session cap. RowSecurity is scoped, so one instance = one
        // request; the memo must collapse repeated (type, action) resolutions to a single invocation.
        var modelLoader = Substitute.For<IModelLoader>();
        var actionsResolver = Substitute.For<IActionsResolver>();
        var actions = new CountingNoteActions(new EntityMapper(modelLoader));
        actionsResolver.ResolveForType(typeof(Note)).Returns(actions);
        var rowSecurity = new RowSecurity(actionsResolver);

        // Ten single-row checks + a 50-row list filter, all action "Read".
        for (var i = 0; i < 10; i++)
            await rowSecurity.IsAllowedAsync(typeof(Note), "Read", new Note { Owner = "alice" });
        using var session = Store.OpenAsyncSession();
        var rows = Enumerable.Range(0, 50).Select(_ => (object)new Note { Owner = "alice" }).ToList();
        await rowSecurity.FilterAsync(session, rows, typeof(Note), typeof(Note), "Read");

        actions.Invocations.Should().Be(1,
            "the hook is memoized per (type, action) per request — invariant of row count");

        // A different action is a distinct key → one more invocation, not zero and not per-row.
        await rowSecurity.IsAllowedAsync(typeof(Note), "Edit", new Note { Owner = "alice" });
        actions.Invocations.Should().Be(2);
    }

    [Fact]
    public async Task ResetRequestFilterCache_forces_the_next_resolution_to_re_invoke_the_hook()
    {
        // M3 (#239): the streaming re-auth tick calls ResetRequestFilterCache so a frozen row
        // filter can't keep serving revoked rows for the socket's life. Proves the reset clears
        // the memo — after it, a shrunk allow-list is re-read.
        var modelLoader = Substitute.For<IModelLoader>();
        var actionsResolver = Substitute.For<IActionsResolver>();
        var actions = new CountingNoteActions(new EntityMapper(modelLoader));
        actionsResolver.ResolveForType(typeof(Note)).Returns(actions);
        var rowSecurity = new RowSecurity(actionsResolver);

        await rowSecurity.IsAllowedAsync(typeof(Note), "Read", new Note { Owner = "alice" });
        await rowSecurity.IsAllowedAsync(typeof(Note), "Read", new Note { Owner = "alice" });
        actions.Invocations.Should().Be(1, "memoized within the request");

        rowSecurity.ResetRequestFilterCache();

        await rowSecurity.IsAllowedAsync(typeof(Note), "Read", new Note { Owner = "alice" });
        actions.Invocations.Should().Be(2, "the reset drops the memo, so the hook re-runs (allow-list re-read)");
    }

    [Fact]
    public async Task The_per_row_can_block_differentiates_edit_from_delete()
    {
        // G5 (#236): read-everyone, edit/delete only your own — the detail page must gate its
        // Edit and Delete buttons independently, which only per-row evaluation can express.
        var rowSecurity = CreateRowSecurity(em => new GuardedNoteActions(em));
        var lockedRow = new Note { Owner = "alice", Title = "Alice's locked" };

        (await rowSecurity.IsAllowedAsync(typeof(Note), "Edit", lockedRow))
            .Should().BeFalse("the predicate refuses Edit on a locked row");
        (await rowSecurity.IsAllowedAsync(typeof(Note), "Delete", lockedRow))
            .Should().BeTrue("but Delete is still allowed — the block must carry both, not one flag");
    }

    [Fact]
    public void A_type_with_no_row_rule_reports_no_row_rule_so_the_can_block_is_skipped()
    {
        var modelLoader = Substitute.For<IModelLoader>();
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Note))
            .Returns(new DefaultPersistentObjectActions<Note>(new EntityMapper(modelLoader)));

        new RowSecurity(actionsResolver).HasRowRule(typeof(Note)).Should().BeFalse(
            "GetPersistentObjectAsync only computes the can block when HasRowRule is true — "
            + "unruled types leave it null and clients fall back to type-level permissions");
    }

    private static RowSecurity CreateRowSecurity<TActions>(Func<IEntityMapper, TActions> factory)
        where TActions : DefaultPersistentObjectActions<Note>
    {
        var modelLoader = Substitute.For<IModelLoader>();
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Note)).Returns(factory(new EntityMapper(modelLoader)));
        return new RowSecurity(actionsResolver);
    }

    private Task SeedNotesAsync()
        => SeedAsync(async session =>
        {
            await session.StoreAsync(new Note { Title = "Alice's first", Owner = "alice" });
            await session.StoreAsync(new Note { Title = "Alice's locked", Owner = "alice" });
            await session.StoreAsync(new Note { Title = "Bob's secret", Owner = "bob" });
        });

    [Fact]
    public async Task The_filter_composes_into_the_raven_query_and_narrows_it_server_side()
    {
        await SeedNotesAsync();
        ScopedNoteActions.CurrentUser = "alice";
        ScopedNoteActions.IsAdmin = false;

        var rowSecurity = CreateRowSecurity(em => new ScopedNoteActions(em));
        using var session = Store.OpenAsyncSession();

        var composed = await rowSecurity.ComposeRowFilterAsync(session.Query<Note>(), typeof(Note), typeof(Note), "Query");

        composed.ToString().Should().Contain("Owner",
            "the predicate must land in the RQL itself — that is what keeps a row-scoped type "
            + "from reading its whole collection");

        var results = await ((IRavenQueryable<Note>)composed).ToListAsync();
        results.Should().HaveCount(2).And.OnlyContain(n => n.Owner == "alice");
    }

    [Fact]
    public async Task Filter_only_mode_derives_single_row_checks_from_the_same_expression()
    {
        ScopedNoteActions.CurrentUser = "alice";
        ScopedNoteActions.IsAdmin = false;
        var rowSecurity = CreateRowSecurity(em => new ScopedNoteActions(em));

        (await rowSecurity.IsAllowedAsync(typeof(Note), "Read", new Note { Owner = "alice" }))
            .Should().BeTrue();
        (await rowSecurity.IsAllowedAsync(typeof(Note), "Read", new Note { Owner = "bob" }))
            .Should().BeFalse("list and detail derive from one expression and cannot diverge");

        rowSecurity.HasRowRule(typeof(Note)).Should().BeTrue(
            "a GetRowFilter override is a row rule — the strict unverifiable-row handling must apply");
    }

    [Fact]
    public async Task A_null_filter_means_no_restriction_for_this_caller()
    {
        await SeedNotesAsync();
        ScopedNoteActions.CurrentUser = "alice";
        ScopedNoteActions.IsAdmin = true;
        var rowSecurity = CreateRowSecurity(em => new ScopedNoteActions(em));

        using var session = Store.OpenAsyncSession();
        var notes = (await session.Query<Note>().ToListAsync()).Cast<object>().ToList();
        var visible = await rowSecurity.FilterAsync(session, notes, typeof(Note), typeof(Note), "Query");

        visible.Should().HaveCount(3, "an administrator's GetRowFilter returns null — nothing is restricted");
        (await rowSecurity.IsAllowedAsync(typeof(Note), "Edit", new Note { Owner = "bob" })).Should().BeTrue();
    }

    [Fact]
    public async Task A_constant_false_filter_denies_everything_without_query_translation()
    {
        await SeedNotesAsync();
        ScopedNoteActions.CurrentUser = null;
        ScopedNoteActions.IsAdmin = false;
        var rowSecurity = CreateRowSecurity(em => new ScopedNoteActions(em));

        using var session = Store.OpenAsyncSession();

        // The constant predicate must not be pushed into RQL (the provider may not translate it) …
        var queryable = session.Query<Note>();
        (await rowSecurity.ComposeRowFilterAsync(queryable, typeof(Note), typeof(Note), "Query"))
            .Should().BeSameAs(queryable);

        // … and the in-memory evaluation still drops every row.
        var notes = (await session.Query<Note>().ToListAsync()).Cast<object>().ToList();
        var visible = await rowSecurity.FilterAsync(session, notes, typeof(Note), typeof(Note), "Query");
        visible.Should().BeEmpty();
    }

    [Fact]
    public async Task Both_members_overridden_means_both_must_allow()
    {
        var rowSecurity = CreateRowSecurity(em => new GuardedNoteActions(em));

        var lockedOwnRow = new Note { Owner = "alice", Title = "Alice's locked" };
        var openOwnRow = new Note { Owner = "alice", Title = "Alice's first" };
        var foreignRow = new Note { Owner = "bob", Title = "Bob's open" };

        (await rowSecurity.IsAllowedAsync(typeof(Note), "Read", lockedOwnRow)).Should().BeTrue();
        (await rowSecurity.IsAllowedAsync(typeof(Note), "Edit", lockedOwnRow)).Should().BeFalse(
            "the filter admits the owner but the predicate refuses Edit on a locked row — AND semantics");
        (await rowSecurity.IsAllowedAsync(typeof(Note), "Edit", openOwnRow)).Should().BeTrue();
        (await rowSecurity.IsAllowedAsync(typeof(Note), "Edit", foreignRow)).Should().BeFalse(
            "the filter already excludes foreign rows regardless of what the predicate would say");
    }

    [Fact]
    public async Task A_projection_falls_back_to_the_batched_reload_with_the_compiled_filter()
    {
        ScopedNoteActions.CurrentUser = "alice";
        ScopedNoteActions.IsAdmin = false;
        var rowSecurity = CreateRowSecurity(em => new ScopedNoteActions(em));

        string aliceId, bobId;
        using (var session = Store.OpenAsyncSession())
        {
            var alice = new Note { Title = "Alice's", Owner = "alice" };
            var bob = new Note { Title = "Bob's", Owner = "bob" };
            await session.StoreAsync(alice);
            await session.StoreAsync(bob);
            await session.SaveChangesAsync();
            aliceId = alice.Id!;
            bobId = bob.Id!;
        }

        using var filterSession = Store.OpenAsyncSession();

        // The filter cannot compose into a projection query …
        var queryable = filterSession.Query<Note>();
        (await rowSecurity.ComposeRowFilterAsync(queryable, typeof(Note), typeof(VNote), "Query"))
            .Should().BeSameAs(queryable);

        // … so FilterAsync loads the base documents (one batch) and judges those.
        var projections = new List<object>
        {
            new VNote { Id = aliceId, Title = "Alice's" },
            new VNote { Id = bobId, Title = "Bob's" },
        };
        var visible = await rowSecurity.FilterAsync(
            filterSession, projections, typeof(Note), typeof(VNote), "Query");

        visible.Should().ContainSingle().Which.Should().BeOfType<VNote>()
            .Which.Title.Should().Be("Alice's");
        filterSession.Advanced.NumberOfRequests.Should().Be(1);
    }

    // --- E2E through the real QueryExecutor, filter-only actions class -----------------------

    private static readonly Guid NoteTypeId = Guid.Parse("c7c7c7c7-7777-7777-7777-c7c7c7c7c7c7");

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Note> Notes => Session.Query<Note>();
    }

    [Fact]
    public async Task A_list_query_through_the_executor_returns_only_the_callers_rows()
    {
        await SeedNotesAsync();
        ScopedNoteActions.CurrentUser = "alice";
        ScopedNoteActions.IsAdmin = false;

        var modelLoader = Substitute.For<IModelLoader>();
        var contextResolver = Substitute.For<ISparkContextResolver>();
        var indexCatalog = Substitute.For<IIndexCatalog>();
        var permissionService = Substitute.For<IPermissionService>();
        var actionsResolver = Substitute.For<IActionsResolver>();
        var referenceResolver = Substitute.For<IReferenceResolver>();

        var entityMapper = new EntityMapper(modelLoader);
        var noteDef = new EntityTypeDefinition
        {
            Id = NoteTypeId,
            Name = "Note",
            ClrType = typeof(Note).FullName!,
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string", Order = 1 },
            ],
            Breadcrumb = "{Title}",
        };
        modelLoader.GetEntityTypeByClrType(typeof(Note).FullName!).Returns(noteDef);
        modelLoader.GetEntityType(noteDef.Id).Returns(noteDef);

        contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns(ci =>
        {
            var ctx = new TestContext();
            typeof(SparkContext).GetProperty(nameof(SparkContext.Session))!
                .SetValue(ctx, ci.Arg<IAsyncDocumentSession>());
            return ctx;
        });
        indexCatalog.GetByIndexName(Arg.Any<string>()).Returns((IndexCatalogEntry?)null);
        referenceResolver.GetReferenceProperties(Arg.Any<Type>(), Arg.Any<Type>())
            .Returns(new List<(PropertyInfo Property, ReferenceAttribute Attribute)>());
        actionsResolver.ResolveForType(typeof(Note)).Returns(new ScopedNoteActions(entityMapper));

        var rowSecurity = new RowSecurity(actionsResolver);
        using var session = Store.OpenAsyncSession();
        var executor = new QueryExecutor(
            session, entityMapper, modelLoader, contextResolver, indexCatalog,
            permissionService, actionsResolver, referenceResolver,
            new BreadcrumbResolver(modelLoader, new BreadcrumbClosure(modelLoader), rowSecurity, new SparkOptions()),
            rowSecurity);

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "GetNotes",
            Source = "Database.Notes",
            EntityType = "Note",
        };
        var result = await executor.ExecuteQueryAsync(query);

        result.Items.Should().HaveCount(2);
        result.TotalItems.Should().Be(2, "totals must be computed on the filtered set");
        result.Items.Select(po => po.Values.Single(a => a.Key == "Title").Value?.ToString())
            .Should().BeEquivalentTo(["Alice's first", "Alice's locked"]);
    }
}
