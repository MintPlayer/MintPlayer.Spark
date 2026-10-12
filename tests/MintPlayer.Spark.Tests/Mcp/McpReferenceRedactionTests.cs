using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Authorization;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// MCP PRD D19 (commit 911fd4b8): a reference to a type the caller may not <c>Read</c> is hidden like an
/// attribute-level denial. The MCP tools reach it through the shared paths (catalogue, query and object
/// read paths, all on <c>AttributeRightsEnforcement</c>), so an agent sees exactly what the web shows the
/// same caller: the references are absent from <c>get</c>, <c>describe</c> and <c>query</c> alike.
/// Fixtures: <see cref="ReferenceTargetRightsTests"/>' <c>RtCar</c> (references <c>RtDriver</c>).
/// </summary>
public class McpReferenceRedactionTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private static readonly Guid CarTypeId = Guid.Parse("4c9d0000-00a1-4000-8000-0000000000a1");
    private static readonly Guid DriverTypeId = Guid.Parse("4c9d0000-00a2-4000-8000-0000000000a2");
    private static readonly Guid CarsQueryId = Guid.Parse("4c9d0000-00a3-4000-8000-0000000000a3");
    private const string CarId = "rtcars/mcp-1";
    private static readonly string[] References = ["Driver", "Passengers", "Owner"];

    private static IEnumerable<EntityTypeFile> Models() =>
    [
        new()
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = CarTypeId,
                Name = "RtCar",
                ClrType = typeof(RtCar).FullName!,
                Breadcrumb = "{Name}",
                AgentPermissions = EAgentPermission.Read,
                Attributes =
                [
                    new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1 },
                    new() { Id = Guid.NewGuid(), Name = "Driver", DataType = "Reference", ReferenceType = typeof(RtDriver).FullName, Order = 2 },
                    new() { Id = Guid.NewGuid(), Name = "Passengers", DataType = "Reference", IsArray = true, ReferenceType = typeof(RtDriver).FullName, Order = 3 },
                    new() { Id = Guid.NewGuid(), Name = "Owner", DataType = "Reference", ReferenceType = typeof(RtDriver).FullName, Order = 4 },
                ],
            },
            Queries =
            [
                new SparkQuery
                {
                    Id = CarsQueryId, Name = "RtCars", Alias = "rtcars", Source = "Database.Cars", EntityType = "RtCar",
                    Columns = [new() { Name = "Name" }, new() { Name = "Driver" }],
                },
            ],
        },
        new()
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = DriverTypeId,
                Name = "RtDriver",
                ClrType = typeof(RtDriver).FullName!,
                Breadcrumb = "{Name}",
                AgentPermissions = EAgentPermission.Read,
                Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
            },
        },
    ];

    private async Task<SparkEndpointFactory<RtContext>> StartAsync(params string[] grants)
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new RtDriver { Name = "Dora" }, "rtdrivers/mcp-1");
            await session.StoreAsync(new RtCar { Name = "Alpha", Driver = "rtdrivers/mcp-1", Passengers = ["rtdrivers/mcp-1"], Owner = "rtdrivers/mcp-1" }, CarId);
            await session.SaveChangesAsync();
        }

        return McpTestHost.Create<RtContext>(Store, Models(), SparkTestSecurity.Empty.Granting(grants),
            configureServices: services =>
            {
                services.AddScoped<RtCarActions>();
                services.AddScoped<RtDriverActions>();
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_shows_a_reference_exactly_when_the_web_does(bool mayReadDriver)
    {
        await using var factory = await StartAsync(mayReadDriver ? ["QueryRead/RtCar", "Read/RtDriver"] : ["QueryRead/RtCar"]);
        var client = factory.CreateClient();

        using var web = await client.PostAsJsonAsync("/spark/po/load", new { objectTypeId = "RtCar", id = CarId });
        web.StatusCode.Should().Be(HttpStatusCode.OK);
        var webNames = (await web.Content.ReadFromJsonAsync<JsonObject>())!["attributes"]!.AsArray()
            .Select(a => a!["name"]!.GetValue<string>()).Order().ToList();

        var mcp = await CallAsync(client, "acme_get", new { type = "RtCar", id = CarId });
        mcp.IsToolError.Should().BeFalse(mcp.ToolText);
        var mcpNames = Unfence(mcp.ToolText)["attributes"]!.AsObject().Select(a => a.Key).Order().ToList();

        mcpNames.Should().Equal(webNames, "an agent sees the attributes the web shows the same caller");
        if (mayReadDriver)
            References.All(mcpNames.Contains).Should().BeTrue("with Read on the target the references are shown");
        else
            References.Any(mcpNames.Contains).Should().BeFalse("a reference to an unreadable type is hidden like a denied attribute (D19)");
    }

    [Fact]
    public async Task Describe_and_query_hide_a_reference_to_an_unreadable_type()
    {
        await using var factory = await StartAsync("QueryRead/RtCar");
        var client = factory.CreateClient();

        var describe = await CallAsync(client, "acme_describe", new { type = "RtCar" });
        await CallAsync(client, "acme_query", new { query = "rtcars" }); // creates the auto index
        await WaitForIndexesAsync();
        var query = await CallAsync(client, "acme_query", new { query = "rtcars" });

        describe.IsToolError.Should().BeFalse(describe.ToolText);
        Unfence(describe.ToolText)["attributes"]!.AsArray().Select(a => a!["name"]!.GetValue<string>())
            .Should().Equal(["Name"], "describe uses the catalogue, which shapes the type with the reference denials");

        query.IsToolError.Should().BeFalse(query.ToolText);
        var page = Unfence(query.ToolText);
        page["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).Should().NotContain("Driver");
        page["rows"]!.AsArray().SelectMany(r => r!["values"]!.AsObject().Select(v => v.Key)).Should().NotContain("Driver");
        query.ToolText.Should().NotContain("rtdrivers/mcp-1", "the hidden reference's target id must not leak either");
    }

    /// <summary>
    /// A sort or a filter on a hidden reference answers what the web answers the same caller (§6.2, D19 "sort/filter";
    /// the web side is <c>ReferenceTargetRightsTests</c>): ignored, so neither the order of the rows nor their count says
    /// anything about the hidden target ids. Two cars whose drivers' ids order against their names, so a sort that was
    /// applied would show.
    /// </summary>
    [Fact]
    public async Task A_sort_or_a_filter_on_a_hidden_reference_changes_nothing_as_on_the_web()
    {
        await using var factory = await StartAsync("QueryRead/RtCar");
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new RtCar { Name = "Sort A", Driver = "rtdrivers/mcp-sort-2" }, "rtcars/mcp-sort-a");
            await session.StoreAsync(new RtCar { Name = "Sort B", Driver = "rtdrivers/mcp-sort-1" }, "rtcars/mcp-sort-b");
            await session.SaveChangesAsync();
        }
        var client = factory.CreateClient();
        await CallAsync(client, "acme_query", new { query = "rtcars" }); // creates the auto index
        await WaitForIndexesAsync();
        var sort = new[] { new { property = "Driver", direction = "desc" } };
        var filters = new[] { new { name = "Driver", includes = new[] { "rtdrivers/mcp-sort-1" } } };

        var plain = await RowsAsync(await CallAsync(client, "acme_query", new { query = "rtcars" }));
        var sorted = await RowsAsync(await CallAsync(client, "acme_query", new { query = "rtcars", sort }));
        var filtered = await RowsAsync(await CallAsync(client, "acme_query", new { query = "rtcars", filters }));
        var webSorted = await WebRowsAsync(client, new { queryId = "rtcars", sortColumns = sort });
        var webFiltered = await WebRowsAsync(client, new { queryId = "rtcars", columns = filters });

        plain.Ids.Should().Contain("rtcars/mcp-sort-a").And.Contain("rtcars/mcp-sort-b");
        sorted.Ids.Should().Equal(webSorted.Ids, "the agent's sort answers what the web's does");
        sorted.Ids.Should().Equal(plain.Ids, "a sort on the hidden reference is ignored: an applied one is an order oracle on its ids");
        filtered.Total.Should().Be(webFiltered.Total);
        filtered.Total.Should().Be(plain.Total, "a filter on the hidden reference is ignored: an applied one is a count oracle on its ids");
    }

    private static Task<(List<string> Ids, int Total)> RowsAsync(Reply reply)
    {
        reply.IsToolError.Should().BeFalse(reply.ToolText);
        var page = Unfence(reply.ToolText);
        return Task.FromResult((page["rows"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToList(), page["totalItems"]!.GetValue<int>()));
    }

    private static async Task<(List<string> Ids, int Total)> WebRowsAsync(HttpClient client, object request)
    {
        using var response = await client.PostAsJsonAsync("/spark/queries/execute", request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!;
        return (json["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToList(), json["totalItems"]!.GetValue<int>());
    }

    private static JsonNode Unfence(string text)
    {
        var start = text.IndexOf("<data-", StringComparison.Ordinal) + 1;
        var tag = text[start..text.IndexOf('>', start)];
        var open = text.LastIndexOf($"<{tag}>", StringComparison.Ordinal) + tag.Length + 2;
        var close = text.LastIndexOf($"</{tag}>", StringComparison.Ordinal);
        return JsonNode.Parse(text[open..close])!;
    }
}
