using System.Net;
using System.Text.Json;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

public class SparkClientDeleteTests
{
    private static (SparkClient client, ScriptedHttpHandler handler) NewClientWithWarmup()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(
                ".AspNetCore.Antiforgery.abc=val; Path=/",
                "XSRF-TOKEN=token; Path=/");
        var client = new SparkClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, ownsClient: true);
        return (client, handler);
    }

    [Fact]
    public async Task Delete_happy_path_sends_POST_with_antiforgery_header()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.OK);
        using (client)
        {
            var typeId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            await client.DeletePersistentObjectAsync(typeId, "people/1");
        }

        var delete = handler.Requests[^1];
        delete.Method.Should().Be(HttpMethod.Post);
        delete.RequestUri!.AbsolutePath.Should().Be("/spark/po/delete");
        delete.Headers.Contains("X-XSRF-TOKEN").Should().BeTrue();

        // "people/1" used to have to survive percent-encoding into a catch-all route segment. It is
        // a JSON string now and arrives with its slash intact, which is what Raven ids need.
        var body = handler.LastBody();
        body.GetProperty("objectTypeId").GetString().Should().Be("11111111-2222-3333-4444-555555555555");
        body.GetProperty("id").GetString().Should().Be("people/1");
    }

    [Fact]
    public async Task Delete_on_404_throws_SparkClientException_with_NotFound_status()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        using (client)
        {
            var ex = await Assert.ThrowsAsync<SparkClientException>(
                () => client.DeletePersistentObjectAsync(Guid.NewGuid(), "id"));
            ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Delete_on_403_throws_SparkClientException_with_Forbidden_status()
    {
        var (client, handler) = NewClientWithWarmup();
        handler.EnqueueStatus(HttpStatusCode.Forbidden);
        using (client)
        {
            var ex = await Assert.ThrowsAsync<SparkClientException>(
                () => client.DeletePersistentObjectAsync(Guid.NewGuid(), "id"));
            ex.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }
}
