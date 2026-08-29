using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// #327 M11 — narrowing a query run to a set of row ids, which is how a custom action re-materializes
/// a selection.
/// <para>
/// The property that decides the design: re-running the query returns <b>the rows the grid had</b>,
/// carrying the query's own projection. A column the query computes — the stand-in here for a field
/// computed inside a RavenDB index and stored there — exists on no document, so materializing the
/// same selection by id would return it as null. Silently: the mapper skips a property it cannot
/// find, and the projector emits an empty cell.
/// </para>
/// </summary>
public class QueryIdRestrictionTests : SparkTestDriver
{
    private static readonly Guid GadgetTypeId = Guid.Parse("aa11bb22-cc33-dd44-ee55-ff6677889900");
    private static readonly Guid GadgetQueryId = Guid.Parse("bb22cc33-dd44-ee55-ff66-778899001122");

    /// <summary>Composed: no <c>clrType</c>, so there are no documents to fall back to.</summary>
    private static EntityTypeFile GadgetModel(string source = "Custom.GetGadgets", string name = "Gadget") => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = GadgetTypeId,
            Name = name,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "Label", DataType = "string",
                    ShowedOn = EShowedOn.Query | EShowedOn.PersistentObject,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = "ComputedTotal", DataType = "number",
                    ShowedOn = EShowedOn.Query,
                },
            ],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = GadgetQueryId, Name = $"{name}Rows", Source = source, EntityType = name,
                SortColumns = [new SortColumn { Property = "Label", Direction = "asc" }],
            },
        ],
    };

    private static async Task<QueryResult> RunAsync(
        SparkEndpointFactory factory, EntityTypeFile model, string[]? restrictTo, int take = 50)
    {
        // A fresh scope, because IQueryExecutor is scoped like a request.
        using var scope = factory.GetService<IServiceScopeFactory>().CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IQueryExecutor>();
        return await executor.ExecuteQueryAsync(model.Queries[0], take: take, restrictToIds: restrictTo);
    }

    [Fact]
    public async Task Restricting_returns_exactly_the_named_rows()
    {
        var model = GadgetModel();
        await using var factory = new SparkEndpointFactory(Store, [model]);

        var result = await RunAsync(factory, model, ["gadgets/3", "gadgets/1"]);

        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["gadgets/1", "gadgets/3"]);
    }

    [Fact]
    public async Task The_rows_carry_the_querys_own_computed_columns()
    {
        // The whole reason a selection re-runs its query instead of loading documents.
        var model = GadgetModel();
        await using var factory = new SparkEndpointFactory(Store, [model]);

        var result = await RunAsync(factory, model, ["gadgets/2"]);

        var row = result.Items.Single();
        row.Values.Single(v => v.Key == "ComputedTotal").Value!.ToString().Should().Be("200");
        row.Values.Single(v => v.Key == "Label").Value!.ToString().Should().Be("Beta");
    }

    [Fact]
    public async Task Paging_is_ignored_when_restricted()
    {
        // A restricted run is "these rows", not "a page of these rows". Honouring take here is how a
        // bulk action silently acts on the first few of a large selection.
        var model = GadgetModel();
        await using var factory = new SparkEndpointFactory(Store, [model]);

        var result = await RunAsync(factory, model, ["gadgets/1", "gadgets/2", "gadgets/3"], take: 1);

        result.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task An_unknown_id_simply_does_not_come_back()
    {
        // Narrowing never invents a row. The caller's all-or-nothing check turns the short result
        // into a refusal; that decision is not this layer's.
        var model = GadgetModel();
        await using var factory = new SparkEndpointFactory(Store, [model]);

        var result = await RunAsync(factory, model, ["gadgets/1", "gadgets/nope"]);

        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["gadgets/1"]);
    }

    [Fact]
    public async Task An_unrestricted_run_is_unaffected()
    {
        var model = GadgetModel();
        await using var factory = new SparkEndpointFactory(Store, [model]);

        var result = await RunAsync(factory, model, restrictTo: null);

        result.Items.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_queryable_source_is_narrowed_by_the_DEFAULT_filter_with_no_hook()
    {
        // ⚠️ This covers the path every other test in this file skips. GadgetActions declares
        // RestrictToIds, so the hook wins and the framework's own expression is never built — which
        // is how a genuine defect in it (List<string>.Contains is an INSTANCE method, so the list is
        // the receiver and not the first argument) survived a green suite and was found by clicking
        // the demo. WidgetlessActions deliberately declares no hook.
        var model = GadgetModel(source: "Custom.GetQueryableGadgets", name: "Widgetless");
        await using var factory = new SparkEndpointFactory(Store, [model]);

        var result = await RunAsync(factory, model, ["gadgets/1", "gadgets/3"]);

        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["gadgets/1", "gadgets/3"]);
        result.Items.Should().OnlyContain(i => i.Values.Any(v => v.Key == "ComputedTotal"));
    }

    [Fact]
    public async Task A_RAVEN_backed_source_is_narrowed_in_the_database()
    {
        // ⚠️ The third path, and the third bug found by clicking rather than by the suite. RavenDB
        // and LINQ-to-objects need DIFFERENT expressions for the same idea and neither works on the
        // other: Raven wants `x.Id.In(ids)` and fails to translate `ids.Contains(x.Id)` at all.
        // The in-memory test above passes either way, so it could not have caught this.
        var personType = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels.Person(
            Guid.Parse("cc33dd44-ee55-ff66-7788-990011223344"));
        personType.Queries =
        [
            new SparkQuery
            {
                Id = Guid.Parse("dd44ee55-ff66-7788-9900-112233445566"),
                Name = "AllPeople",
                Source = "Database.People",
                EntityType = "Person",
            },
        ];

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.StoreAsync(new Person { FirstName = "Bob", LastName = "Jones" }, "people/2");
            await session.StoreAsync(new Person { FirstName = "Carol", LastName = "Davis" }, "people/3");
        });

        await using var factory = new SparkEndpointFactory(Store, [personType]);
        using var scope = factory.GetService<IServiceScopeFactory>().CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IQueryExecutor>();

        var result = await executor.ExecuteQueryAsync(
            personType.Queries[0], restrictToIds: ["people/1", "people/3"]);

        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["people/1", "people/3"]);
    }

    [Fact]
    public async Task A_plain_sequence_with_a_readable_id_needs_NO_hook()
    {
        // ⚠️ This used to throw, and that was a design error severe enough to make composed queries
        // unusable with custom actions: a List is the most natural way to write one, and nothing
        // about arriving eagerly makes a row's id unfindable. Filtering in memory is also the honest
        // semantic — narrowing at the SOURCE would let a caller name rows the query never returned,
        // and the all-or-nothing count could not tell.
        var model = GadgetModel(source: "Custom.GetPlainList", name: "PlainList");
        await using var factory = new SparkEndpointFactory(Store, [model]);

        var result = await RunAsync(factory, model, ["gadgets/2"]);

        result.Items.Should().ContainSingle().Which.Id.Should().Be("gadgets/2");
    }

    [Fact]
    public async Task A_source_the_framework_cannot_match_on_and_declaring_no_hook_fails_loudly()
    {
        // What genuinely needs the hook: a row whose identity is not a readable STRING Id — here a
        // numeric key, and in the wild a composite one. The framework has nothing to compare, so it
        // must say so rather than return every row and let all-or-nothing report a refusal that
        // reads like a permission problem.
        var model = GadgetModel(source: "Custom.GetUnnarrowable", name: "Unnarrowable");
        await using var factory = new SparkEndpointFactory(Store, [model]);

        var act = () => RunAsync(factory, model, ["gadgets/1"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        ex.Message.Should().Contain("cannot be narrowed to a selection")
            .And.Contain("RestrictToIds")
            .And.Contain("UnnarrowableActions");
    }

    [Fact]
    public async Task A_query_owning_its_own_paging_is_reported_as_such_without_running_it()
    {
        // Answered from the declared return type, so the caller branches rather than trying and
        // failing: there is no way to ask SparkQueryPage for "the page containing these ids".
        var paged = GadgetModel(source: "Custom.GetGadgetPage");
        var plain = GadgetModel();

        await using var factory = new SparkEndpointFactory(Store, [plain]);
        using var scope = factory.GetService<IServiceScopeFactory>().CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IQueryExecutor>();

        executor.OwnsItsOwnPaging(paged.Queries[0]).Should().BeTrue();
        executor.OwnsItsOwnPaging(plain.Queries[0]).Should().BeFalse();
    }
}

/// <summary>Found by name — a composed type's actions class. Rows are computed; nothing is stored.</summary>
public sealed class GadgetActions
{
    private static readonly GadgetRow[] All =
    [
        new("gadgets/1", "Alpha", 100),
        new("gadgets/2", "Beta", 200),
        new("gadgets/3", "Gamma", 300),
    ];

    public IEnumerable<GadgetRow> GetGadgets() => All;

    /// <summary>Author-paged: cannot be re-run narrowed, and must be recognised before invocation.</summary>
    public SparkQueryPage<GadgetRow> GetGadgetPage(CustomQueryArgs args) => new([.. All], All.Length);

    /// <summary>
    /// How to find these rows again by id. Required, because they are computed in memory: there is
    /// no queryable for the framework's default id filter to compose onto, and a row's identity is
    /// whatever this class minted.
    /// </summary>
    public object RestrictToIds(object source, IReadOnlyCollection<string> ids)
        => ((IEnumerable<GadgetRow>)source).Where(g => ids.Contains(g.Id)).ToList();
}

/// <summary>
/// Returns an IQueryable and declares NO hook, so the framework's default id filter is what narrows
/// it. That is the path the demo exercised and the suite did not.
/// </summary>
public sealed class WidgetlessActions
{
    public IQueryable<GadgetRow> GetQueryableGadgets() => new[]
    {
        new GadgetRow("gadgets/1", "Alpha", 100),
        new GadgetRow("gadgets/2", "Beta", 200),
        new GadgetRow("gadgets/3", "Gamma", 300),
    }.AsQueryable();
}

/// <summary>The same shape without the hook — the loud-failure case.</summary>
public sealed class UnnarrowableActions
{
    public IEnumerable<NumericRow> GetUnnarrowable() =>
    [
        new(1, "Alpha", 100),
    ];
}

/// <summary>
/// A plain List with an ordinary string id — the shape of nearly every composed query, and the one
/// that must work with no hook at all.
/// </summary>
public sealed class PlainListActions
{
    public IEnumerable<GadgetRow> GetPlainList() =>
    [
        new("gadgets/1", "Alpha", 100),
        new("gadgets/2", "Beta", 200),
    ];
}

/// <summary>Rows the framework cannot match a submitted id against — a non-string key.</summary>
public sealed record NumericRow(int Id, string Label, int ComputedTotal);

public sealed record GadgetRow(string Id, string Label, int ComputedTotal);
