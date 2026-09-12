using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// <see cref="SparkClient.ExecuteQueryAsync(System.Guid,int,int,string,string,string,System.Threading.CancellationToken)"/>
/// and its alias overload build the request themselves. The endpoint only parses what arrives, so a
/// dropped or misspelled field here would silently produce incorrect behaviour on a real server.
/// These tests assert on exactly what the client puts on the wire.
/// </summary>
/// <remarks>
/// ⚠️ They used to assert on a URL and a query string, and now assert on a JSON body — the reads
/// became POSTs so their hooks could prompt, and so that column filtering has somewhere to live. The
/// escaping facts went with the change rather than being ported: there is nothing left to escape,
/// which is the point. An alias containing a slash is now just a string in a field.
/// </remarks>
public class SparkClientQueryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (SparkClient client, ScriptedHttpHandler handler) NewClient()
    {
        var handler = new ScriptedHttpHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new SparkClient(http, ownsClient: true);
        return (client, handler);
    }

    private static HttpResponseMessage EmptyQueryResult() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new QueryResult
        {
            Columns = [],
            Items = [],
            TotalItems = 0,
            Skip = 0,
            Take = 50,
        }, options: JsonOptions),
    };

    [Fact]
    public async Task Execute_by_guid_targets_expected_path_and_default_pagination()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(EmptyQueryResult());
        using (client)
        {
            var queryId = Guid.Parse("abcabcab-abcd-abcd-abcd-abcabcabcabc");
            await client.ExecuteQueryAsync(queryId);
        }

        var req = handler.Requests.Single();
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.AbsolutePath.Should().Be("/spark/queries/execute");

        var body = handler.LastBody();
        body.GetProperty("queryId").GetString().Should().Be("abcabcab-abcd-abcd-abcd-abcabcabcabc");
        body.GetProperty("skip").GetInt32().Should().Be(0);
        body.GetProperty("take").GetInt32().Should().Be(50);
        body.GetProperty("search").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("parentId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("parentType").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_alias_with_a_slash_travels_in_the_body_unescaped()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(EmptyQueryResult());
        using (client)
        {
            // This alias used to need percent-encoding to avoid looking like path traversal. In a
            // JSON field it needs nothing, and must arrive byte-for-byte as written — the server
            // resolves it as a name, so an escaped copy would resolve to nothing.
            await client.ExecuteQueryAsync("my alias/with-slash");
        }

        var req = handler.Requests.Single();
        req.RequestUri!.AbsolutePath.Should().Be("/spark/queries/execute");
        handler.LastBody().GetProperty("queryId").GetString().Should().Be("my alias/with-slash");
    }

    [Fact]
    public async Task All_optional_parameters_surface_as_body_fields()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(EmptyQueryResult());
        using (client)
        {
            await client.ExecuteQueryAsync(
                Guid.Parse("11111111-0000-0000-0000-000000000001"),
                skip: 10,
                take: 25,
                search: "Alice & Bob",
                parentId: "people/1",
                parentType: "Person");
        }

        var body = handler.LastBody();
        body.GetProperty("skip").GetInt32().Should().Be(10);
        body.GetProperty("take").GetInt32().Should().Be(25);
        // The "&" needed encoding as %26 in a query string or it split the parameters. In a JSON
        // string it is an ordinary character, and must survive as one.
        body.GetProperty("search").GetString().Should().Be("Alice & Bob");
        body.GetProperty("parentId").GetString().Should().Be("people/1");
        body.GetProperty("parentType").GetString().Should().Be("Person");
    }

    [Fact]
    public async Task Sort_columns_travel_as_an_array_not_a_colon_separated_string()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(EmptyQueryResult());
        using (client)
        {
            await client.ExecuteQueryAsync(Guid.NewGuid(), sortColumns: "Name:asc,RegisteredAt:desc");
        }

        var columns = (handler.LastBody())
            .GetProperty("sortColumns")
            .EnumerateArray()
            .Select(c => (c.GetProperty("property").GetString(), c.GetProperty("direction").GetString()))
            .ToArray();

        columns.Should().Equal([("Name", "asc"), ("RegisteredAt", "desc")],
            "the parameter keeps its published string shape, but the encoding it carried is gone from the wire");
    }

    [Fact]
    public async Task Execute_throws_on_non_success_status_with_status_preserved()
    {
        var (client, handler) = NewClient();
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        using (client)
        {
            var ex = await Assert.ThrowsAsync<SparkClientException>(
                () => client.ExecuteQueryAsync(Guid.NewGuid()));
            ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Execute_read_path_does_not_warm_up_antiforgery()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(EmptyQueryResult());
        using (client)
        {
            await client.ExecuteQueryAsync(Guid.NewGuid());
        }

        // ⚠️ Still true now that the read is a POST, and deliberately so: the read endpoints carry no
        // antiforgery metadata, because the verb changed and what they do did not. If a warmup
        // appears here, someone added RequireAntiforgeryTokenAttribute to a read.
        handler.Requests.Should().ContainSingle();
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/spark/queries/execute");
    }

}
