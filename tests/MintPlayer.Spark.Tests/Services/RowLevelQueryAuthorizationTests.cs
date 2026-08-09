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
/// M5 — row-level authorization on the query path.
/// <para>
/// The type-level check answers "may this principal query this type at all". It says nothing about
/// which rows, and the query path asked nothing further. Spark's list screens go through the query
/// path, so an entity whose Actions class scoped rows to their owner was filtered correctly when a
/// row was opened and disclosed in full on the screen that lists every row at once. The rule was
/// written once and enforced in some places.
/// </para>
/// <para>
/// These tests use the <b>real</b> <see cref="RowSecurity"/> against a real Actions class, because
/// the defect was never in the rule — it was in which paths consulted it.
/// </para>
/// </summary>
public class RowLevelQueryAuthorizationTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("a5a5a5a5-5555-5555-5555-a5a5a5a5a5a5");

    public class Note
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
    }

    /// <summary>The worked example: rows belong to their owner, exactly as the Fleet demo does it.</summary>
    public class NoteActions : DefaultPersistentObjectActions<Note>
    {
        /// <summary>Stands in for the signed-in principal.</summary>
        public static string CurrentUser = "alice";

        public NoteActions(IEntityMapper entityMapper) : base(entityMapper) { }

        public override Task<bool> IsAllowedAsync(string action, Note entity)
            => Task.FromResult(entity.Owner == CurrentUser);
    }

    /// <summary>An entity with no rule at all — must keep behaving exactly as before.</summary>
    public class Memo
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Note> Notes => Session.Query<Note>();
        public IRavenQueryable<Memo> Memos => Session.Query<Memo>();
    }

    private readonly IModelLoader _modelLoader = Substitute.For<IModelLoader>();
    private readonly ISparkContextResolver _contextResolver = Substitute.For<ISparkContextResolver>();
    private readonly IIndexRegistry _indexRegistry = Substitute.For<IIndexRegistry>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly IActionsResolver _actionsResolver = Substitute.For<IActionsResolver>();
    private readonly IReferenceResolver _referenceResolver = Substitute.For<IReferenceResolver>();

    private static EntityTypeDefinition Definition(string name, Guid id, Type clr) => new()
    {
        Id = id,
        Name = name,
        ClrType = clr.FullName!,
        Attributes =
        [
            new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string", Order = 1 },
        ],
        Breadcrumb = "{Title}",
    };

    private QueryExecutor CreateExecutor()
    {
        var entityMapper = new EntityMapper(_modelLoader);
        var noteDef = Definition("Note", NoteTypeId, typeof(Note));
        var memoDef = Definition("Memo", Guid.Parse("b6b6b6b6-6666-6666-6666-b6b6b6b6b6b6"), typeof(Memo));

        _modelLoader.GetEntityTypeByClrType(typeof(Note).FullName!).Returns(noteDef);
        _modelLoader.GetEntityTypeByClrType(typeof(Memo).FullName!).Returns(memoDef);
        _modelLoader.GetEntityType(noteDef.Id).Returns(noteDef);
        _modelLoader.GetEntityType(memoDef.Id).Returns(memoDef);

        _contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns(ci =>
        {
            var ctx = new TestContext();
            typeof(SparkContext).GetProperty(nameof(SparkContext.Session))!
                .SetValue(ctx, ci.Arg<IAsyncDocumentSession>());
            return ctx;
        });

        _indexRegistry.GetRegistrationForCollectionType(Arg.Any<Type>()).Returns((IndexRegistration?)null);
        _referenceResolver.GetReferenceProperties(Arg.Any<Type>(), Arg.Any<Type>())
            .Returns(new List<(PropertyInfo Property, ReferenceAttribute Attribute)>());

        // The real resolver, so the real Actions class is found by the real convention.
        _actionsResolver.ResolveForType(typeof(Note)).Returns(new NoteActions(entityMapper));
        _actionsResolver.ResolveForType(typeof(Memo)).Returns(new DefaultPersistentObjectActions<Memo>(entityMapper));

        var permissive = Substitute.For<IRowSecurity>();
        permissive.IsAllowedAsync(default!, default!, default!).ReturnsForAnyArgs(true);

        var session = Store.OpenAsyncSession();
        return new QueryExecutor(
            session, entityMapper, _modelLoader, _contextResolver, _indexRegistry,
            _permissionService, _actionsResolver, _referenceResolver,
            new BreadcrumbResolver(_modelLoader, new BreadcrumbClosure(_modelLoader), permissive, new SparkOptions()),
            new RowSecurity(_actionsResolver));
    }

    private async Task SeedNotesAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new Note { Title = "Alice's first", Owner = "alice" });
        await session.StoreAsync(new Note { Title = "Alice's second", Owner = "alice" });
        await session.StoreAsync(new Note { Title = "Bob's secret", Owner = "bob" });
        await session.SaveChangesAsync();
        WaitForIndexing(Store);
    }

    private static SparkQuery Query(string source, string entityType) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Get" + source,
        Source = "Database." + source,
        EntityType = entityType,
    };

    [Fact]
    public async Task A_list_query_returns_only_the_callers_rows()
    {
        await SeedNotesAsync();
        NoteActions.CurrentUser = "alice";

        var results = (await CreateExecutor().ExecuteQueryAsync(Query("Notes", "Note"))).Data.ToList();

        results.Should().HaveCount(2,
            "the list screen shows every row at once — it is the worst place for a row rule to be skipped");
        results.Select(r => r.Attributes.Single(a => a.Name == "Title").Value?.ToString())
            .Should().BeEquivalentTo(["Alice's first", "Alice's second"]);
    }

    [Fact]
    public async Task Another_users_rows_are_not_disclosed()
    {
        await SeedNotesAsync();
        NoteActions.CurrentUser = "bob";

        var results = (await CreateExecutor().ExecuteQueryAsync(Query("Notes", "Note"))).Data.ToList();

        results.Should().ContainSingle()
            .Which.Attributes.Single(a => a.Name == "Title").Value?.ToString()
            .Should().Be("Bob's secret");
    }

    [Fact]
    public async Task A_caller_who_owns_nothing_sees_nothing()
    {
        await SeedNotesAsync();
        NoteActions.CurrentUser = "mallory";

        var results = (await CreateExecutor().ExecuteQueryAsync(Query("Notes", "Note"))).Data.ToList();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task An_entity_with_no_row_rule_is_unaffected()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Memo { Title = "Public one" });
            await session.StoreAsync(new Memo { Title = "Public two" });
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);

        var results = (await CreateExecutor().ExecuteQueryAsync(Query("Memos", "Memo"))).Data.ToList();

        results.Should().HaveCount(2,
            "types that never declared a rule must behave exactly as before — that is what makes "
            + "failing closed on the unverifiable cases safe to turn on");
    }

    [Fact]
    public void HasRowRule_distinguishes_an_override_from_the_inherited_default()
    {
        var rowSecurity = new RowSecurity(_actionsResolver);
        var entityMapper = new EntityMapper(_modelLoader);
        _actionsResolver.ResolveForType(typeof(Note)).Returns(new NoteActions(entityMapper));
        _actionsResolver.ResolveForType(typeof(Memo)).Returns(new DefaultPersistentObjectActions<Memo>(entityMapper));

        rowSecurity.HasRowRule(typeof(Note)).Should().BeTrue();
        rowSecurity.HasRowRule(typeof(Memo)).Should().BeFalse(
            "the permissive default is not a rule, and treating it as one would put every type "
            + "through the strict unverifiable-row handling for no reason");
    }
}
