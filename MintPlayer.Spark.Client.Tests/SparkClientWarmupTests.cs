using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// The antiforgery warmup is lazy (primes on the first mutating call) and brittle: it relies
/// on the server returning both a <c>.AspNetCore.Antiforgery.*</c> cookie and a readable
/// <c>XSRF-TOKEN</c> cookie via Set-Cookie. These tests pin the two failure shapes — "no
/// cookies at all" and "some cookies but no XSRF" — because an unclear error message there
/// is the hardest kind of test-infrastructure failure to debug.
/// </summary>
public class SparkClientWarmupTests
{
    private static SparkClient NewClient(ScriptedHttpHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, ownsClient: true);

    [Fact]
    public async Task Warmup_throws_when_response_has_no_Set_Cookie_headers()
    {
        // Mutating calls (Delete here) trigger the warmup → the handler's first response
        // is the warmup GET; we return a bare 200 with no cookies, which is the "this server
        // doesn't even try to mint CSRF" shape.
        var handler = new ScriptedHttpHandler().EnqueueOk();
        using var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.DeletePersistentObjectAsync(Guid.NewGuid(), "id"));

        ex.Message.Should().Contain("XSRF-TOKEN");
    }

    [Fact]
    public async Task Warmup_throws_when_response_sets_an_unrelated_cookie_but_no_XSRF()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies("random-cookie=value; Path=/");
        using var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<SparkClientException>(
            () => client.DeletePersistentObjectAsync(Guid.NewGuid(), "id"));

        ex.Message.Should().Contain("XSRF-TOKEN");
    }

    [Fact]
    public async Task Warmup_succeeds_when_XSRF_cookie_is_present_and_attaches_token_to_next_mutation()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(
                ".AspNetCore.Antiforgery.abc=validation-cookie-value; Path=/",
                "XSRF-TOKEN=fake-xsrf-token; Path=/")
            .EnqueueStatus(HttpStatusCode.OK);  // the DELETE that follows warmup
        using var client = NewClient(handler);

        await client.DeletePersistentObjectAsync(Guid.NewGuid(), "my-id");

        // First request: the warmup GET.
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().EndWith("__warmup__");
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);

        // Second request: the DELETE, which must carry X-XSRF-TOKEN.
        var delete = handler.Requests[1];
        delete.Method.Should().Be(HttpMethod.Delete);
        delete.Headers.TryGetValues("X-XSRF-TOKEN", out var xsrfValues).Should().BeTrue();
        xsrfValues!.Should().ContainSingle().Which.Should().Be("fake-xsrf-token");
        delete.Headers.TryGetValues("Cookie", out var cookieValues).Should().BeTrue();
        cookieValues!.Single().Should().Contain("XSRF-TOKEN=fake-xsrf-token");
    }

    [Fact]
    public async Task Warmup_runs_once_per_client_and_is_cached_for_subsequent_mutations()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(
                ".AspNetCore.Antiforgery.abc=validation-cookie-value; Path=/",
                "XSRF-TOKEN=fake-xsrf-token; Path=/")
            .EnqueueStatus(HttpStatusCode.OK)
            .EnqueueStatus(HttpStatusCode.OK);
        using var client = NewClient(handler);

        await client.DeletePersistentObjectAsync(Guid.NewGuid(), "id-a");
        await client.DeletePersistentObjectAsync(Guid.NewGuid(), "id-b");

        // 1 warmup + 2 deletes — no second warmup call.
        handler.Requests.Should().HaveCount(3);
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("__warmup__")).Should().Be(1);
    }
}
