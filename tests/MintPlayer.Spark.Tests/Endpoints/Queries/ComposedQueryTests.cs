using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

/// <summary>
/// Composed queries (#327 M5): a query whose entity type declares no <c>clrType</c>. There is no
/// entity class, no collection and no document behind a row — the rows are computed by the
/// name-resolved <c>{Name}Actions</c> class, the same seam the virtual-type page path uses
/// (<c>VirtualObjectEndpointTests</c> is the sibling).
/// <para>
/// What these pin down is mostly the <em>consequences</em>: row security cannot run (nothing to
/// judge), streaming is refused (no collection to watch), and the type-level right still applies
/// (that check was never row-shaped).
/// </para>
/// </summary>
public class ComposedQueryTests : SparkTestDriver
{
    private static readonly Guid DashboardTypeId = Guid.Parse("77777777-cccc-cccc-cccc-777777777777");
    private static readonly Guid DashboardQueryId = Guid.Parse("88888888-cccc-cccc-cccc-888888888888");

    /// <summary>
    /// The composed shape: no clrType at all, and — unlike the two virtual types in the demos —
    /// attributes that are shown on a query, because that is what gives the grid its columns.
    /// </summary>
    private static EntityTypeFile DashboardModel(
        string name = "Dashboard",
        Guid? typeId = null,
        Guid? queryId = null,
        string source = "Custom.GetRows",
        bool streaming = false,
        EShowedOn showedOn = EShowedOn.Query | EShowedOn.PersistentObject) => new()
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = typeId ?? DashboardTypeId,
                Name = name,
                Breadcrumb = "{Label}",
                Attributes =
                [
                    new EntityAttributeDefinition
                    {
                        Id = Guid.NewGuid(), Name = "Label", DataType = "string", ShowedOn = showedOn,
                    },
                    new EntityAttributeDefinition
                    {
                        Id = Guid.NewGuid(), Name = "Amount", DataType = "number", ShowedOn = showedOn,
                    },
                ],
            },
            Queries =
            [
                new SparkQuery
                {
                    Id = queryId ?? DashboardQueryId,
                    Name = $"{name}Rows",
                    Source = source,
                    EntityType = name,
                    IsStreamingQuery = streaming,
                },
            ],
        };

    private static async Task<QueryResult> ExecuteAsync(
        SparkEndpointFactory factory, Guid queryId, int skip = 0, int take = 50,
        string? search = null, string? sortColumns = null)
    {
        using var client = new SparkClient(factory.CreateClient(), ownsClient: true);
        return await client.ExecuteQueryAsync(queryId, skip, take, search, sortColumns: sortColumns);
    }

    [Fact]
    public async Task Composed_query_renders_rows_from_the_name_resolved_actions_class()
    {
        await using var factory = new SparkEndpointFactory(Store, [DashboardModel()]);

        var result = await ExecuteAsync(factory, DashboardQueryId);

        result.TotalItems.Should().Be(3, "no CLR entity exists for this type — DashboardActions computed the rows");
        result.Columns.Should().HaveCount(2, "columns come from the attributes marked ShowedOn.Query");
        result.Columns.Select(c => c.Name).Should().BeEquivalentTo(["Label", "Amount"]);
        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["row/1", "row/2", "row/3"]);
        result.Items[0].Values.Should().Contain(v => v.Key == "Label" && v.Value!.ToString() == "Revenue");
    }

    [Fact]
    public async Task Composed_rows_render_the_model_breadcrumb_template()
    {
        await using var factory = new SparkEndpointFactory(Store, [DashboardModel()]);

        var result = await ExecuteAsync(factory, DashboardQueryId);

        result.Items[0].Breadcrumb.Should().Be("Revenue",
            "the '{Label}' template renders over the computed row, exactly as it does over a document");
    }

    [Fact]
    public void A_row_carries_no_affordance_to_close()
    {
        // The PRD listed "square the per-row envelope closed (Can = none)" under this milestone.
        // M4 settled it structurally instead: a row is a projection, so QueryResultItem has no
        // "can" block and no etag at all — there is nothing to force to false, on this path or any
        // other. Pinned as a type-shape fact so a future addition has to argue with this test.
        typeof(QueryResultItem).GetProperty("Can").Should().BeNull();
        typeof(QueryResultItem).GetProperty("Etag").Should().BeNull();
    }

    [Fact]
    public async Task Composed_query_still_runs_under_the_type_level_Query_right()
    {
        // The one check that survives: "may this principal query this type at all" is not a
        // row-shaped question, so having no documents does not exempt it.
        var perms = Substitute.For<IPermissionService>();
        perms.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));
        perms.EnsureAuthorizedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(x => Task.FromException(new SparkAccessDeniedException($"{x.ArgAt<string>(0)}/{x.ArgAt<string>(1)}")));

        await using var factory = new SparkEndpointFactory(Store, [DashboardModel()], services =>
        {
            services.RemoveAll<IPermissionService>();
            services.AddSingleton(perms);
        });

        var ex = await Assert.ThrowsAsync<SparkClientException>(() => ExecuteAsync(factory, DashboardQueryId));

        ex.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Composed_type_without_an_actions_class_fails_loudly()
    {
        // Nothing named OrphanBoardActions exists. On the PAGE path that is a 404 — the type simply
        // has no page. A QUERY is different: the model declares one, so something is meant to serve
        // it, and an empty grid would hide the fact that nothing does.
        var orphan = DashboardModel(
            name: "OrphanBoard",
            typeId: Guid.Parse("99999999-cccc-cccc-cccc-999999999999"),
            queryId: Guid.Parse("aaaaaaaa-cccc-cccc-cccc-aaaaaaaaaaaa"));

        await using var factory = new SparkEndpointFactory(Store, [orphan]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteAsync(factory, Guid.Parse("aaaaaaaa-cccc-cccc-cccc-aaaaaaaaaaaa")));

        ex.Message.Should().Contain("OrphanBoardActions").And.Contain("no source at all");
    }

    [Fact]
    public async Task A_composed_type_carrying_a_query_must_show_something_on_it()
    {
        // R8: both virtual types in the wild are PersistentObject-only on every attribute, and
        // copying one is exactly what an author adding a query to a composed type will do. Rows
        // with no columns is a blank grid that looks like an empty result.
        var invisible = DashboardModel(
            name: "InvisibleBoard",
            typeId: Guid.Parse("bbbbbbbb-cccc-cccc-cccc-bbbbbbbbbbbb"),
            queryId: Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            showedOn: EShowedOn.PersistentObject);

        var problems = SparkComposedQueries.Validate(
            [invisible.PersistentObject], invisible.Queries);

        problems.Should().ContainSingle()
            .Which.Should().Contain("showedOn").And.Contain("InvisibleBoard");
    }

    [Fact]
    public void Streaming_over_a_composed_type_is_refused_by_the_shared_rule()
    {
        // F16: this used to die at the first MoveNext inside an open websocket, as
        // `CLR type '' not found` wrapped in `{"message":"Stream failed"}` — no query name, no
        // reason, and invisible until someone opened the page. The rule is shared with
        // --spark-verify-model, so CI refuses the model before it ever runs.
        var streamer = DashboardModel(
            name: "StreamBoard",
            typeId: Guid.Parse("dddddddd-cccc-cccc-cccc-dddddddddddd"),
            queryId: Guid.Parse("eeeeeeee-cccc-cccc-cccc-eeeeeeeeeeee"),
            streaming: true);

        var problems = SparkComposedQueries.Validate([streamer.PersistentObject], streamer.Queries);

        problems.Should().ContainSingle()
            .Which.Should().Contain("no collection to watch").And.Contain("StreamBoard");
    }

    [Fact]
    public void An_entity_backed_type_is_not_composed_and_raises_nothing()
    {
        var backed = DashboardModel(name: "Backed", streaming: true);
        backed.PersistentObject.ClrType = "MintPlayer.Spark.Tests.Endpoints.Queries.SomeEntity";

        SparkComposedQueries.Validate([backed.PersistentObject], backed.Queries).Should().BeEmpty();
        SparkComposedQueries.Announce(backed.Queries[0], backed.PersistentObject).Should().BeNull();
    }

    [Fact]
    public void Every_composed_query_announces_what_it_opts_out_of()
    {
        var model = DashboardModel();

        var line = SparkComposedQueries.Announce(model.Queries[0], model.PersistentObject);

        // The announcement is the containment for the real risk, which is not the deliberate
        // landing page: it is the next developer reaching for a composed query because it is
        // easier than a row rule, over data that does have owners.
        line.Should().NotBeNull();
        line.Should().Contain("COMPOSED").And.Contain("DashboardActions");
        line.Should().Contain("Row filtering", "the point of the line is naming what does not apply");
    }

    [Fact]
    public async Task Duplicate_row_ids_from_a_composed_query_throw_rather_than_collapsing()
    {
        // In memory there is no fan-out, so DistinctBy does not run here — and must not: it treats
        // every null key as equal and would collapse the grid to one row. A repeated id is an
        // authoring bug in the actions class, and the projector refuses it.
        await using var factory = new SparkEndpointFactory(Store, [DashboardModel(source: "Custom.GetDuplicateRows")]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(factory, DashboardQueryId));

        ex.Message.Should().Contain("two rows with the id 'row/1'");
    }

    [Fact]
    public async Task Sort_columns_are_honoured_on_a_composed_query()
    {
        await using var factory = new SparkEndpointFactory(Store, [DashboardModel()]);

        var result = await ExecuteAsync(factory, DashboardQueryId, sortColumns: "Amount:desc");

        result.Items.Select(i => i.Id).Should().ContainInOrder("row/2", "row/3", "row/1");
    }

    // ----------------------------------------------------------------------------------
    // SparkQueryPage — the author's own page, and the binary authority rule
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task SparkQueryPage_reports_the_authors_total_not_the_page_length()
    {
        await using var factory = new SparkEndpointFactory(Store, [DashboardModel(source: "Custom.GetPagedRows")]);

        var result = await ExecuteAsync(factory, DashboardQueryId, skip: 0, take: 2);

        result.Items.Should().HaveCount(2, "the method returned its own page");
        result.TotalItems.Should().Be(500,
            "counting the returned rows would report the page size as the total and offer one page");
    }

    [Fact]
    public async Task SparkQueryPage_is_not_paged_again_by_the_framework()
    {
        // The author already applied skip/take. Applying it a second time would silently serve the
        // first N rows of page 3 as page 3 — right count, wrong rows.
        await using var factory = new SparkEndpointFactory(Store, [DashboardModel(source: "Custom.GetPagedRows")]);

        var result = await ExecuteAsync(factory, DashboardQueryId, skip: 10, take: 2);

        result.Items.Should().HaveCount(2);
        result.Items.Select(i => i.Id).Should().BeEquivalentTo(["page/10", "page/11"],
            because: "the method received skip=10 through CustomQueryArgs and honoured it");
    }

    [Fact]
    public async Task SparkQueryPage_keeps_its_own_ordering_when_the_request_names_a_sort()
    {
        // The binary rule's sharpest edge: sorting a page the author already trimmed would present
        // a page-local ordering as a global one — every page internally sorted, the sequence across
        // pages wrong, and nothing about the result saying so.
        await using var factory = new SparkEndpointFactory(Store, [DashboardModel(source: "Custom.GetPagedRows")]);

        var result = await ExecuteAsync(factory, DashboardQueryId, skip: 0, take: 3, sortColumns: "Amount:desc");

        result.Items.Select(i => i.Id).Should().ContainInOrder("page/0", "page/1", "page/2");
    }

    [Fact]
    public async Task SparkQueryPage_keeps_its_own_result_when_the_request_carries_a_search()
    {
        await using var factory = new SparkEndpointFactory(Store, [DashboardModel(source: "Custom.GetPagedRows")]);

        var result = await ExecuteAsync(factory, DashboardQueryId, take: 3, search: "nothing-matches-this");

        result.Items.Should().HaveCount(3, "search authority transferred with the page; the method saw the term");
        result.TotalItems.Should().Be(500);
    }
}

/// <summary>
/// Found by NAME — no base class, no CLR entity, nothing registered. The composed-query path
/// resolves <c>{TypeName}Actions</c> exactly as the virtual-type page path does.
/// </summary>
public sealed class DashboardActions
{
    public IEnumerable<DashboardRow> GetRows() =>
    [
        new DashboardRow("row/1", "Revenue", 10),
        new DashboardRow("row/2", "Costs", 30),
        new DashboardRow("row/3", "Margin", 20),
    ];

    /// <summary>Two rows claiming the same id — an authoring bug, refused rather than collapsed.</summary>
    public IEnumerable<DashboardRow> GetDuplicateRows() =>
    [
        new DashboardRow("row/1", "Revenue", 10),
        new DashboardRow("row/1", "Revenue again", 20),
    ];

    /// <summary>
    /// The escape hatch: a source the framework cannot page for us. Honours skip/take/search
    /// itself, and reports the total of the full result rather than of this page.
    /// </summary>
    public SparkQueryPage<DashboardRow> GetPagedRows(CustomQueryArgs args)
    {
        var rows = Enumerable.Range(args.Skip, Math.Max(args.Take, 0))
            .Select(i => new DashboardRow($"page/{i}", $"Item {i}", i))
            .ToList();
        return new SparkQueryPage<DashboardRow>(rows, 500);
    }
}

/// <summary>A computed row: no collection, no document, no entity class.</summary>
public sealed record DashboardRow(string Id, string Label, double Amount);

/// <summary>Exists only so the "entity-backed types are not composed" case has a clrType to name.</summary>
public sealed class SomeEntity
{
    public string? Id { get; set; }
}
