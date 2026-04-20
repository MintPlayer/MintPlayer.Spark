using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Streaming;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Endpoints.Queries;

public class StreamExecuteQueryTests : IAsyncLifetime
{
    private static readonly Guid PersonTypeId = Guid.NewGuid();

    private readonly IQueryLoader _queryLoader = Substitute.For<IQueryLoader>();
    private readonly IStreamingQueryExecutor _executor = Substitute.For<IStreamingQueryExecutor>();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddSingleton(_queryLoader);
                    services.AddSingleton(_executor);
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/stream/{id}", async httpContext =>
                        {
                            var endpoint = new StreamExecuteQuery(_queryLoader, _executor);
                            var result = await endpoint.HandleAsync(httpContext);
                            await result.ExecuteAsync(httpContext);
                        });
                    });
                }))
            .Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Returns_400_when_the_request_is_not_a_WebSocket_upgrade()
    {
        var client = _host.GetTestClient();

        var response = await client.GetAsync("/stream/q-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("WebSocket connection required");
    }

    [Fact]
    public async Task Returns_404_when_the_query_id_does_not_resolve()
    {
        _queryLoader.ResolveQuery("missing").Returns((SparkQuery?)null);

        var wsClient = _host.GetTestServer().CreateWebSocketClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wsClient.ConnectAsync(
            new Uri(_host.GetTestServer().BaseAddress, "/stream/missing"), CancellationToken.None));

        // TestServer surfaces a pre-upgrade HTTP rejection as "Incomplete handshake, status code: NNN"
        ex.Message.Should().Contain("404");
    }

    [Fact]
    public async Task Returns_400_when_the_query_is_not_flagged_as_streaming()
    {
        var nonStreaming = new SparkQuery { Id = Guid.NewGuid(), Name = "AllPeople", Source = "Database.People", IsStreamingQuery = false };
        _queryLoader.ResolveQuery("all-people").Returns(nonStreaming);

        var wsClient = _host.GetTestServer().CreateWebSocketClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wsClient.ConnectAsync(
            new Uri(_host.GetTestServer().BaseAddress, "/stream/all-people"), CancellationToken.None));

        ex.Message.Should().Contain("400");
    }

    [Fact]
    public async Task Streams_a_snapshot_followed_by_a_patch_over_the_WebSocket()
    {
        var query = new SparkQuery { Id = Guid.NewGuid(), Name = "LivePeople", Source = "Database.People", IsStreamingQuery = true };
        _queryLoader.ResolveQuery("live-people").Returns(query);
        _executor.ExecuteStreamingQueryAsync(query, Arg.Any<CancellationToken>()).Returns(Produce(
            [Po("people/1", ("FirstName", "Alice"))],
            [Po("people/1", ("FirstName", "Alicia"))]
        ));

        var socket = await ConnectAsync("/stream/live-people");

        var first = await ReceiveJsonAsync(socket);
        first.RootElement.GetProperty("type").GetString().Should().Be("snapshot");
        first.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);

        var second = await ReceiveJsonAsync(socket);
        second.RootElement.GetProperty("type").GetString().Should().Be("patch");
        var updated = second.RootElement.GetProperty("updated");
        updated.GetArrayLength().Should().Be(1);
        updated[0].GetProperty("attributes").GetProperty("FirstName").GetString().Should().Be("Alicia");

        await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task Skips_identical_snapshots_and_only_sends_the_initial_one()
    {
        var query = new SparkQuery { Id = Guid.NewGuid(), Name = "SteadyPeople", Source = "Database.People", IsStreamingQuery = true };
        _queryLoader.ResolveQuery("steady").Returns(query);
        _executor.ExecuteStreamingQueryAsync(query, Arg.Any<CancellationToken>()).Returns(Produce(
            [Po("people/1", ("FirstName", "Alice"))],
            [Po("people/1", ("FirstName", "Alice"))]
        ));

        var socket = await ConnectAsync("/stream/steady");

        var first = await ReceiveJsonAsync(socket);
        first.RootElement.GetProperty("type").GetString().Should().Be("snapshot");

        await ExpectCloseAsync(socket, WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task SparkAccessDeniedException_is_delivered_as_an_error_message_then_closes_with_InternalServerError()
    {
        var query = new SparkQuery { Id = Guid.NewGuid(), Name = "Guarded", Source = "Database.People", IsStreamingQuery = true };
        _queryLoader.ResolveQuery("guarded").Returns(query);
        _executor.ExecuteStreamingQueryAsync(query, Arg.Any<CancellationToken>())
            .Returns(Throwing(new SparkAccessDeniedException("nope")));

        var socket = await ConnectAsync("/stream/guarded");

        var message = await ReceiveJsonAsync(socket);
        message.RootElement.GetProperty("type").GetString().Should().Be("error");
        message.RootElement.GetProperty("message").GetString().Should().Be("Access denied");

        await ExpectCloseAsync(socket, WebSocketCloseStatus.InternalServerError);
    }

    [Fact]
    public async Task Generic_exception_is_surfaced_as_an_error_message_with_the_exception_text()
    {
        var query = new SparkQuery { Id = Guid.NewGuid(), Name = "Crashy", Source = "Database.People", IsStreamingQuery = true };
        _queryLoader.ResolveQuery("crashy").Returns(query);
        _executor.ExecuteStreamingQueryAsync(query, Arg.Any<CancellationToken>())
            .Returns(Throwing(new InvalidOperationException("boom")));

        var socket = await ConnectAsync("/stream/crashy");

        var message = await ReceiveJsonAsync(socket);
        message.RootElement.GetProperty("type").GetString().Should().Be("error");
        message.RootElement.GetProperty("message").GetString().Should().Be("boom");

        await ExpectCloseAsync(socket, WebSocketCloseStatus.InternalServerError);
    }

    private async Task<WebSocket> ConnectAsync(string path)
    {
        var wsClient = _host.GetTestServer().CreateWebSocketClient();
        return await wsClient.ConnectAsync(
            new Uri(_host.GetTestServer().BaseAddress, path),
            CancellationToken.None);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(WebSocket socket)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"Socket closed before a message arrived: {result.CloseStatus} {result.CloseStatusDescription}");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    private static async Task ExpectCloseAsync(WebSocket socket, WebSocketCloseStatus expected)
    {
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        result.MessageType.Should().Be(WebSocketMessageType.Close);
        socket.CloseStatus.Should().Be(expected);
    }

    private static global::MintPlayer.Spark.Abstractions.PersistentObject Po(string id, params (string Name, object? Value)[] attrs) => new()
    {
        Id = id,
        Name = id,
        ObjectTypeId = PersonTypeId,
        Attributes = attrs.Select(a => new global::MintPlayer.Spark.Abstractions.PersistentObjectAttribute { Name = a.Name, Value = a.Value }).ToArray(),
    };

    private static async IAsyncEnumerable<global::MintPlayer.Spark.Abstractions.PersistentObject[]> Produce(params global::MintPlayer.Spark.Abstractions.PersistentObject[][] batches)
    {
        foreach (var batch in batches)
        {
            yield return batch;
            await Task.Yield();
        }
    }

#pragma warning disable CS1998 // no awaits — the iterator is expected to throw synchronously on MoveNext
    private static async IAsyncEnumerable<global::MintPlayer.Spark.Abstractions.PersistentObject[]> Throwing(Exception ex)
    {
        throw ex;
        yield break;
    }
#pragma warning restore CS1998
}
