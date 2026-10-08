using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Endpoints.LookupReferences;
using TestModels = MintPlayer.Spark.Tests.Endpoints.PersistentObject.TestModels;

namespace MintPlayer.Spark.Tests.Endpoints;

/// <summary>
/// The answers of Spark core's body-reading endpoints — persistent objects, queries and custom actions —
/// to the five bodies no endpoint can bind, and to one well-formed request each (endpoints generator
/// completion M3b, PRD D3a). Measured while the endpoints read their bodies through
/// <c>SparkRequestType.ReadAsync</c>/<c>SparkRequestBody</c>, and kept when they became typed endpoints.
/// "throws" is an exception the application left unhandled, which the test server rethrows and a real
/// server answers with a 500.
/// </summary>
/// <remarks>
/// The one deliberate change (owner-accepted, PRD D3a): "none" and "text/plain" escaped as an unhandled
/// <c>InvalidOperationException</c> (a 500) while the endpoints read by hand; typed, they get the same
/// answer as every other unbindable body, from <c>OnBindFailedAsync</c>.
/// </remarks>
public class UnbindableCoreBodiesTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("3b3b0000-0000-4000-8000-000000000001");
    private static readonly Guid AllPeopleQueryId = Guid.Parse("3b3b0000-0000-4000-8000-000000000011");

    private SparkEndpointFactory _factory = null!;
    private SparkClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var model = TestModels.Person(PersonTypeId);
        model.Queries =
        [
            new SparkQuery { Id = AllPeopleQueryId, Name = "AllPeople", Source = "Database.People" },
        ];
        _factory = new SparkEndpointFactory(Store, [model], configureServices: ConfigureServices);
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Person { FirstName = "Alice", LastName = "Smith" }, "people/1");
            await session.StoreAsync(new Person { FirstName = "Bob", LastName = "Jones" }, "people/2");
            await session.StoreAsync(new Person { FirstName = "Carol", LastName = "White" }, "people/3");
        });
    }

    /// <summary>The host's extra services; none here, MVC in <see cref="UnbindableCoreBodiesWithMvcTests"/>.</summary>
    protected virtual void ConfigureServices(IServiceCollection services) { }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static readonly string[] Endpoints =
    [
        "/spark/po/load",
        "/spark/po/new",
        "/spark/po/refresh",
        "/spark/po/create",
        "/spark/po/update",
        "/spark/po/delete",
        "/spark/po/delete-many",
        "/spark/po/delete-row",
        "/spark/queries/get",
        "/spark/queries/execute",
        "/spark/queries/distinct-values",
        "/spark/actions/list",
        "/spark/actions/execute",
    ];

    [Fact]
    public async Task Unbindable_bodies_answer_as_before()
    {
        var lines = new List<string>();
        foreach (var url in Endpoints)
        {
            lines.Add($"# {url}");
            foreach (var (label, content) in LookupReferenceEndpointTests.UnbindableBodies())
                lines.Add(await LookupReferenceEndpointTests.Line(label,
                    () => _client.SendAsync(HttpMethod.Post, url, content, requiresAntiforgery: true)));
        }

        string.Join("\n", lines).Should().Be(ExpectedUnbindable.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Well_formed_requests_answer_as_before()
    {
        var type = PersonTypeId.ToString();
        var query = AllPeopleQueryId.ToString();
        var lines = new List<string>();

        async Task<JsonNode?> Send(string label, string url, object body)
        {
            using var response = await _client.SendAsync(HttpMethod.Post, url, JsonContent.Create(body), requiresAntiforgery: true);
            var text = await response.Content.ReadAsStringAsync();
            lines.Add($"{label}: {(int)response.StatusCode}");
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }

        static JsonNode Unwrap(JsonNode? node) => (node?["result"] ?? node)!.DeepClone();

        var loaded = Unwrap(await Send("load", "/spark/po/load", new { objectTypeId = type, id = "people/1" }));
        var scaffold = Unwrap(await Send("new", "/spark/po/new", new { objectTypeId = type }));
        await Send("refresh", "/spark/po/refresh", new { objectTypeId = type, persistentObject = scaffold });
        await Send("create", "/spark/po/create", new { objectTypeId = type, persistentObject = scaffold.DeepClone() });
        await Send("update", "/spark/po/update", new { objectTypeId = type, id = "people/1", persistentObject = loaded });

        var second = Unwrap(await Send("load people/2", "/spark/po/load", new { objectTypeId = type, id = "people/2" }));
        await Send("delete", "/spark/po/delete", new { objectTypeId = type, id = "people/2", etag = second["etag"]!.GetValue<string>() });

        var third = Unwrap(await Send("load people/3", "/spark/po/load", new { objectTypeId = type, id = "people/3" }));
        await Send("delete-many", "/spark/po/delete-many", new { objectTypeId = type, queryId = query, items = new[] { new { id = "people/3", etag = third["etag"]!.GetValue<string>() } } });
        await Send("delete-row", "/spark/po/delete-row", new { objectTypeId = type, asDetailAttribute = "Nope", parentType = type, parentId = "people/1", rowKey = "x" });

        await Send("queries/get", "/spark/queries/get", new { queryId = query });
        await Send("queries/execute", "/spark/queries/execute", new { queryId = query });
        await Send("queries/distinct-values", "/spark/queries/distinct-values", new { queryId = query, column = "FirstName" });
        await Send("actions/list", "/spark/actions/list", new { objectTypeId = type });
        await Send("actions/execute", "/spark/actions/execute", new { objectTypeId = type, actionName = "Nope" });

        string.Join("\n", lines).Should().Be(ExpectedWellFormed.ReplaceLineEndings("\n"));
    }

    private const string ExpectedUnbindable = """
        # /spark/po/load
        none: 404 {"error":"Not found"}
        empty: 404 {"error":"Not found"}
        null: 404 {"error":"Not found"}
        malformed: 404 {"error":"Not found"}
        text/plain: 404 {"error":"Not found"}
        # /spark/po/new
        none: 404 {"result":{"error":"Not found"},"operations":[]}
        empty: 404 {"result":{"error":"Not found"},"operations":[]}
        null: 404 {"result":{"error":"Not found"},"operations":[]}
        malformed: 404 {"result":{"error":"Not found"},"operations":[]}
        text/plain: 404 {"result":{"error":"Not found"},"operations":[]}
        # /spark/po/refresh
        none: 404 {"result":{"error":"Not found"},"operations":[]}
        empty: 404 {"result":{"error":"Not found"},"operations":[]}
        null: 404 {"result":{"error":"Not found"},"operations":[]}
        malformed: 404 {"result":{"error":"Not found"},"operations":[]}
        text/plain: 404 {"result":{"error":"Not found"},"operations":[]}
        # /spark/po/create
        none: 404 {"result":{"error":"Not found"},"operations":[]}
        empty: 404 {"result":{"error":"Not found"},"operations":[]}
        null: 404 {"result":{"error":"Not found"},"operations":[]}
        malformed: 404 {"result":{"error":"Not found"},"operations":[]}
        text/plain: 404 {"result":{"error":"Not found"},"operations":[]}
        # /spark/po/update
        none: 404 {"result":{"error":"Not found"},"operations":[]}
        empty: 404 {"result":{"error":"Not found"},"operations":[]}
        null: 404 {"result":{"error":"Not found"},"operations":[]}
        malformed: 404 {"result":{"error":"Not found"},"operations":[]}
        text/plain: 404 {"result":{"error":"Not found"},"operations":[]}
        # /spark/po/delete
        none: 404 {"result":{"error":"Not found"},"operations":[]}
        empty: 404 {"result":{"error":"Not found"},"operations":[]}
        null: 404 {"result":{"error":"Not found"},"operations":[]}
        malformed: 404 {"result":{"error":"Not found"},"operations":[]}
        text/plain: 404 {"result":{"error":"Not found"},"operations":[]}
        # /spark/po/delete-many
        none: 404 {"result":{"error":"Not found"},"operations":[]}
        empty: 404 {"result":{"error":"Not found"},"operations":[]}
        null: 404 {"result":{"error":"Not found"},"operations":[]}
        malformed: 404 {"result":{"error":"Not found"},"operations":[]}
        text/plain: 404 {"result":{"error":"Not found"},"operations":[]}
        # /spark/po/delete-row
        none: 404 {"result":{"error":"Not found"},"operations":[]}
        empty: 404 {"result":{"error":"Not found"},"operations":[]}
        null: 404 {"result":{"error":"Not found"},"operations":[]}
        malformed: 404 {"result":{"error":"Not found"},"operations":[]}
        text/plain: 404 {"result":{"error":"Not found"},"operations":[]}
        # /spark/queries/get
        none: 404 {"error":"Query not found"}
        empty: 404 {"error":"Query not found"}
        null: 404 {"error":"Query not found"}
        malformed: 404 {"error":"Query not found"}
        text/plain: 404 {"error":"Query not found"}
        # /spark/queries/execute
        none: 404 {"error":"Query not found"}
        empty: 404 {"error":"Query not found"}
        null: 404 {"error":"Query not found"}
        malformed: 404 {"error":"Query not found"}
        text/plain: 404 {"error":"Query not found"}
        # /spark/queries/distinct-values
        none: 404 {"error":"Query not found"}
        empty: 404 {"error":"Query not found"}
        null: 404 {"error":"Query not found"}
        malformed: 404 {"error":"Query not found"}
        text/plain: 404 {"error":"Query not found"}
        # /spark/actions/list
        none: 200 []
        empty: 200 []
        null: 200 []
        malformed: 200 []
        text/plain: 200 []
        # /spark/actions/execute
        none: 404 {"result":{"error":"Not found"},"operations":[]}
        empty: 404 {"result":{"error":"Not found"},"operations":[]}
        null: 404 {"result":{"error":"Not found"},"operations":[]}
        malformed: 404 {"result":{"error":"Not found"},"operations":[]}
        text/plain: 404 {"result":{"error":"Not found"},"operations":[]}
        """;

    private const string ExpectedWellFormed = """
        load: 200
        new: 200
        refresh: 200
        create: 201
        update: 200
        load people/2: 200
        delete: 204
        load people/3: 200
        delete-many: 204
        delete-row: 404
        queries/get: 200
        queries/execute: 200
        queries/distinct-values: 200
        actions/list: 200
        actions/execute: 404
        """;
}

/// <summary>
/// The same answers in a host that calls <c>AddControllers()</c>, as every application does: there a
/// typed endpoint binds its body through MVC's input formatters instead of <c>ReadFromJsonAsync</c>, so
/// this proves the two binders agree on every pinned case.
/// </summary>
public class UnbindableCoreBodiesWithMvcTests : UnbindableCoreBodiesTests
{
    protected override void ConfigureServices(IServiceCollection services) => services.AddControllers();
}
