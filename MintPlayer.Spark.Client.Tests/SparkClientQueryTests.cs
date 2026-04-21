using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// <see cref="SparkClient.ExecuteQueryAsync(System.Guid,int,int,string,string,string,System.Threading.CancellationToken)"/>
/// and its alias overload build the request URL and query string themselves. The endpoint
/// only parses what arrives, so wrong encoding here (forgetting to URI-escape, dropping a
/// param, mis-ordering) would silently produce incorrect behaviour on a real server. These
/// tests assert on exactly what the client puts on the wire.
/// </summary>
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
            Data = Array.Empty<PersistentObject>(),
            TotalRecords = 0,
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
        req.Method.Should().Be(HttpMethod.Get);
        req.RequestUri!.AbsolutePath.Should().Be("/spark/queries/abcabcab-abcd-abcd-abcd-abcabcabcabc/execute");
        var qs = req.RequestUri!.Query;
        qs.Should().Contain("skip=0").And.Contain("take=50");
        qs.Should().NotContain("search=").And.NotContain("parentId=").And.NotContain("parentType=");
    }

    [Fact]
    public async Task Execute_by_alias_URI_encodes_the_alias_segment()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(EmptyQueryResult());
        using (client)
        {
            // Alias contains a character that requires percent-encoding to avoid path-traversal
            // looking syntax in the URL — prove the client escapes it.
            await client.ExecuteQueryAsync("my alias/with-slash");
        }

        var req = handler.Requests.Single();
        req.RequestUri!.AbsolutePath.Should().Be("/spark/queries/my%20alias%2Fwith-slash/execute");
    }

    [Fact]
    public async Task All_optional_parameters_surface_as_query_string_values()
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

        var qs = handler.Requests.Single().RequestUri!.Query;
        qs.Should().Contain("skip=10");
        qs.Should().Contain("take=25");
        qs.Should().Contain("search=Alice%20%26%20Bob");     // "&" URL-encoded to %26 so it doesn't split the querystring
        qs.Should().Contain("parentId=people%2F1");
        qs.Should().Contain("parentType=Person");
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

        // Reads don't need CSRF → only one HTTP call, the GET itself.
        handler.Requests.Should().ContainSingle();
        handler.Requests.Single().RequestUri!.AbsolutePath.Should().NotEndWith("__warmup__");
    }
}
