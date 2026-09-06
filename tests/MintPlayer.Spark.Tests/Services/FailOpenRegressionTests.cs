using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Services.Breadcrumb;
using MintPlayer.Spark.Tests._Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Regression detectors for three fail-open paths (PRD F1-F3). Each one served rows, or ran an
/// action, in a state where the framework had silently stopped enforcing something — and each was
/// invisible to the existing suite, which is the property these tests remove.
/// </summary>
public class FailOpenRegressionTests
{
    // ---------- F3: a mismatched actions class must not degrade to the permissive default -------

    public sealed class MismatchedDoc
    {
        public string? Id { get; set; }
    }

    /// <summary>
    /// Named for <see cref="MismatchedDoc"/> but implementing nothing. The realistic cause is a
    /// generic argument that drifted after a rename — the file still reads as though it guards the
    /// type.
    /// </summary>
    public sealed class MismatchedDocActions
    {
        public Task<bool> IsAllowedAsync(string action, MismatchedDoc entity) => Task.FromResult(false);
    }

    /// <summary>
    /// Before the fix this returned <c>DefaultPersistentObjectActions&lt;MismatchedDoc&gt;</c>: the
    /// class above was found by name, failed the cast, and control fell through to the framework
    /// default. Every hook on it is permissive, <c>IsOverridden</c> reports false, <c>HasRowRule</c>
    /// reports false — so the type was served completely unrestricted, with no diagnostic anywhere,
    /// while a file named <c>MismatchedDocActions</c> sat in the repository refusing everything.
    /// </summary>
    [Fact]
    public void An_actions_class_that_does_not_implement_the_contract_is_refused_loudly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IAsyncDocumentSession>());
        var resolver = new ActionsResolver(services.BuildServiceProvider());

        var act = () => resolver.Resolve<MismatchedDoc>();

        act.Should().Throw<InvalidOperationException>(
                "falling back to the permissive default would silently drop the row rule this class " +
                "was written to apply")
            .Where(e => e.Message.Contains(nameof(MismatchedDocActions))
                     && e.Message.Contains(nameof(MismatchedDoc)));
    }

    // ---------- F1: TotalItems must never count rows the caller cannot see ----------------------

    public sealed class PagedDoc
    {
        public string? Id { get; set; }
        public string? Owner { get; set; }
    }

    public sealed class PagedDocActions(IEntityMapper entityMapper)
        : DefaultPersistentObjectActions<PagedDoc>(entityMapper, null!)
    {
        /// <summary>Takes over paging and the count — and therefore takes over nothing about security.</summary>
        public Task<SparkQueryPage<PagedDoc>> GetPage(CustomQueryArgs args)
            => Task.FromResult(new SparkQueryPage<PagedDoc>(
                [new PagedDoc { Id = "PagedDocs/1", Owner = "alice" }],
                500));
    }

    /// <summary>
    /// The author reports 500; row security then removes rows from the page the framework holds.
    /// The rows were filtered and the count was not, so <c>TotalItems</c> disclosed the cardinality
    /// of rows the caller may not see — three lines from the comment asserting that row security is
    /// not part of what the author takes over.
    /// <para>
    /// It cannot be repaired by recounting: the framework holds one page and cannot know how many of
    /// the author's other rows would survive. So the combination is refused, which is the only shape
    /// in which the count is never wrong.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Author_paging_is_refused_on_a_row_ruled_type()
    {
        var modelLoader = Substitute.For<IModelLoader>();
        var actionsResolver = Substitute.For<IActionsResolver>();
        var rowSecurity = Substitute.For<IRowSecurity>();

        var definition = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "PagedDoc",
            ClrType = typeof(PagedDoc).AssemblyQualifiedName,
        };

        modelLoader.GetEntityTypeByName("PagedDoc").Returns(definition);
        modelLoader.GetEntityTypeByClrType(Arg.Any<string>()).Returns(definition);
        var pagedActions = new PagedDocActions(Substitute.For<IEntityMapper>());
        actionsResolver.ResolveByEntityName("PagedDoc").Returns(pagedActions);
        actionsResolver.ResolveForType(typeof(PagedDoc)).Returns(pagedActions);

        // The condition under test: this type carries a row rule.
        rowSecurity.HasRowRule(typeof(PagedDoc)).Returns(true);
        rowSecurity.FilterAsync(
                Arg.Any<IAsyncDocumentSession>(), Arg.Any<IReadOnlyList<object>>(),
                Arg.Any<Type>(), Arg.Any<Type>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyList<object>>()));

        var entityMapper = Substitute.For<IEntityMapper>();
        var executor = new QueryExecutor(
            Substitute.For<IAsyncDocumentSession>(), entityMapper, modelLoader,
            Substitute.For<ISparkContextResolver>(), Substitute.For<IIndexCatalog>(),
            Substitute.For<IPermissionService>(), actionsResolver,
            Substitute.For<IReferenceResolver>(), Substitute.For<IBreadcrumbResolver>(), rowSecurity,
            TestRowSecurityGate.For(rowSecurity, entityMapper));

        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "PagedDocs",
            Source = "Custom.GetPage",
            EntityType = "PagedDoc",
        };

        var act = () => executor.ExecuteQueryAsync(query, parent: null, skip: 0, take: 25);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "a count the framework cannot stand behind must not be reported at all"))
            .Where(e => e.Message.Contains("SparkQueryPage") && e.Message.Contains("row rule"));
    }

    // ---------- M2: a Database.* source cannot serve as a sub-query ----------------------------

    private static (QueryExecutor Executor, IPermissionService Permissions) DatabaseSubQuerySetup()
    {
        var modelLoader = Substitute.For<IModelLoader>();
        var permissions = Substitute.For<IPermissionService>();

        modelLoader.GetEntityTypeByName("Car").Returns(new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Car",
            ClrType = typeof(PagedDoc).AssemblyQualifiedName,
        });

        var entityMapper = Substitute.For<IEntityMapper>();
        var executor = new QueryExecutor(
            Substitute.For<IAsyncDocumentSession>(), entityMapper, modelLoader,
            Substitute.For<ISparkContextResolver>(), Substitute.For<IIndexCatalog>(),
            permissions, Substitute.For<IActionsResolver>(),
            Substitute.For<IReferenceResolver>(), Substitute.For<IBreadcrumbResolver>(),
            new PermissiveRowSecurity(),
            TestRowSecurityGate.For(new PermissiveRowSecurity(), entityMapper));

        return (executor, permissions);
    }

    private static SparkQuery DatabaseQuery() => new()
    {
        Id = Guid.NewGuid(),
        Name = "AllCars",
        Source = "Database.Cars",
        EntityType = "Car",
    };

    /// <summary>
    /// <b>The headline defect.</b> The client sends <c>parentId</c>/<c>parentType</c>, the endpoint
    /// resolves and authorizes the parent, and the database branch used to take no parent parameter
    /// at all — so it served every row of the child collection under the parent's page, silently.
    /// Nothing errored and nothing at startup refused the configuration; it went unnoticed only
    /// because every sub-query in the repository happens to use a <c>Custom.*</c> source.
    /// </summary>
    [Fact]
    public async Task A_Database_source_executed_with_a_parent_is_refused()
    {
        var (executor, _) = DatabaseSubQuerySetup();
        var parent = new PersistentObject { Id = "Companies/1", Name = "Company", ObjectTypeId = Guid.NewGuid() };

        var act = () => executor.ExecuteQueryAsync(DatabaseQuery(), parent, skip: 0, take: 25);

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "serving it would list the whole child collection under one parent's page"))
            .Where(e => e.Message.Contains("sub-query") && e.Message.Contains("Custom."));
    }

    /// <summary>
    /// The refusal must come <b>after</b> authorization, not before.
    /// <para>
    /// Its message names the query's source and entity type. Handing that to a caller with no
    /// <c>Query</c> right would be a configuration-disclosure oracle — the same shape as the
    /// <c>?sortColumns=</c> issue, which was closed by moving authorization above the parser rather
    /// than by validating harder. A denied caller must get the denial.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unauthorized_caller_gets_the_denial_not_the_configuration_error()
    {
        var (executor, permissions) = DatabaseSubQuerySetup();
        permissions
            .EnsureAuthorizedAsync("Query", "Car")
            .Returns(Task.FromException(new SparkAccessDeniedException("Query/Car")));

        var parent = new PersistentObject { Id = "Companies/1", Name = "Company", ObjectTypeId = Guid.NewGuid() };

        var act = () => executor.ExecuteQueryAsync(DatabaseQuery(), parent, skip: 0, take: 25);

        await act.Should().ThrowAsync<SparkAccessDeniedException>(
            "authorization runs first, so a caller who may not query this type learns nothing about " +
            "how the query is configured");
    }
}
