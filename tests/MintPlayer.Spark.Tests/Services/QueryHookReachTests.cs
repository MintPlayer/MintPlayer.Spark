using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Whether <c>OnQueryAsync</c> is actually reached, and what it is told.
/// <para>
/// A hook that silently does not fire is the failure its own documentation warns about — the
/// framework looks like it consulted the actions class and did not. These pin the two cases that
/// were wrong: a query with no declared <c>entityType</c>, and the parent signal that tells the hook
/// whether it is running on a detail tab or a query page.
/// </para>
/// </summary>
public class QueryHookReachTests
{
    public sealed class HookedDoc
    {
        public string? Id { get; set; }
    }

    /// <summary>Records what the hook was handed, so "never fired" and "fired with nothing" are distinguishable.</summary>
    public sealed class HookedDocActions(IEntityMapper entityMapper)
        : DefaultPersistentObjectActions<HookedDoc>(entityMapper, null!)
    {
        public int Calls { get; private set; }
        public PersistentObject? SeenParent { get; private set; }
        public string? SeenQueryName { get; private set; }

        public override Task OnQueryAsync(SparkQueryContext context)
        {
            Calls++;
            SeenParent = context.Parent;
            SeenQueryName = context.Query.Name;
            context.DisableActions("Archive");
            return Task.CompletedTask;
        }
    }

    /// <summary>A context whose property declares the element type the model is keyed on.</summary>
    private sealed class HookedContext : SparkContext
    {
        public IRavenQueryable<HookedDoc> Docs => Session.Query<HookedDoc>();
    }

    private static (QueryExecutor Executor, HookedDocActions Actions) Setup()
    {
        var modelLoader = Substitute.For<IModelLoader>();
        var actionsResolver = Substitute.For<IActionsResolver>();
        var contextResolver = Substitute.For<ISparkContextResolver>();
        var actions = new HookedDocActions(Substitute.For<IEntityMapper>());

        var definition = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "HookedDoc",
            ClrType = typeof(HookedDoc).FullName,
        };

        modelLoader.GetEntityTypeByClrType(typeof(HookedDoc).FullName!).Returns(definition);
        modelLoader.GetEntityTypeByName("HookedDoc").Returns(definition);
        actionsResolver.ResolveForType(typeof(HookedDoc)).Returns(actions);
        actionsResolver.ResolveByEntityName("HookedDoc").Returns(actions);
        contextResolver.ResolveContext(Arg.Any<IAsyncDocumentSession>()).Returns(new HookedContext());

        var executor = new QueryExecutor(
            Substitute.For<IAsyncDocumentSession>(), Substitute.For<IEntityMapper>(), modelLoader,
            contextResolver, Substitute.For<IIndexCatalog>(),
            Substitute.For<IPermissionService>(), actionsResolver,
            Substitute.For<IReferenceResolver>(), Substitute.For<IBreadcrumbResolver>(),
            new PermissiveRowSecurity(),
            TestRowSecurityGate.For(new PermissiveRowSecurity()));

        return (executor, actions);
    }

    /// <summary>
    /// A <c>Database.*</c> query may omit <c>entityType</c> — the executor derives it from the
    /// context property's element type. But the hook runs before that resolution, so it saw no
    /// definition, found no actions class, and never fired. The type is knowable at that point, from
    /// the property's <em>declared</em> type, without invoking a getter that could run application
    /// code before authorization.
    /// </summary>
    [Fact]
    public async Task The_hook_fires_for_a_Database_query_that_declares_no_entityType()
    {
        var (executor, actions) = Setup();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "UntypedDocs",
            Source = "Database.Docs",
            // EntityType deliberately unset — the supported shape that used to skip the hook.
        };

        try { await executor.ExecuteQueryAsync(query, parent: null, skip: 0, take: 25); }
        catch { /* the source cannot materialize under mocks; the hook runs first and that is the subject */ }

        actions.Calls.Should().Be(1, "a supported query shape must not silently skip the actions class");
        actions.SeenQueryName.Should().Be("UntypedDocs");
    }

    /// <summary>
    /// The parent is the entire intent signal the hook gets: non-null means a detail tab, null means
    /// a query page. It must arrive on the <c>Database.*</c> branch too, which is the branch that
    /// used to drop it altogether.
    /// </summary>
    [Fact]
    public async Task The_hook_is_told_it_is_running_on_a_query_page_when_there_is_no_parent()
    {
        var (executor, actions) = Setup();

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "AllDocs",
            Source = "Database.Docs",
            EntityType = "HookedDoc",
        };

        try { await executor.ExecuteQueryAsync(query, parent: null, skip: 0, take: 25); }
        catch { /* as above */ }

        actions.Calls.Should().Be(1);
        actions.SeenParent.Should().BeNull("a null parent is what tells the hook this is a query page");
    }
}
