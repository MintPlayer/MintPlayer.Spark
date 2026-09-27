using System.Net;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests;

/// <summary>
/// <see cref="SparkClient"/>'s own cookie jar: what a response may put in it, and what the next
/// request carries. Observed from the outside, through the <c>Cookie</c> header of the next request.
/// </summary>
public class SparkClientCookieJarTests
{
    private static (SparkClient Client, ScriptedHttpHandler Handler) NewClient()
    {
        var handler = new ScriptedHttpHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://app.example.test/") };
        return (new SparkClient(http, ownsClient: true), handler);
    }

    private static async Task SendAsync(SparkClient client, bool requiresAntiforgery = false)
    {
        using var _ = await client.SendAsync(HttpMethod.Get, "/probe", requiresAntiforgery: requiresAntiforgery);
    }

    private static string? CookieHeaderOf(HttpRequestMessage request)
        => request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;

    [Fact]
    public async Task A_cookie_the_server_sets_rides_the_next_request()
    {
        var (client, handler) = NewClient();
        using (client)
        {
            handler.EnqueueWithCookies("session=abc; Path=/; HttpOnly").EnqueueOk();

            await SendAsync(client);
            await SendAsync(client);

            CookieHeaderOf(handler.Requests[0]).Should().BeNull();
            CookieHeaderOf(handler.Requests[1]).Should().Be("session=abc");
        }
    }

    [Theory]
    [InlineData("Domain=app.example.test", true)]
    [InlineData("Domain=.example.test", true)]
    [InlineData("Domain=example.test", true)]
    [InlineData("Domain=evil.test", false)]
    [InlineData("Domain=notexample.test", false)]
    [InlineData("Domain=", true)]
    public async Task A_cookie_scoped_to_another_domain_is_refused(string domainAttribute, bool kept)
    {
        var (client, handler) = NewClient();
        using (client)
        {
            handler.EnqueueWithCookies($"pinned=1; {domainAttribute}; Path=/").EnqueueOk();

            await SendAsync(client);
            await SendAsync(client);

            if (kept)
                CookieHeaderOf(handler.Requests[1]).Should().Be("pinned=1");
            else
                CookieHeaderOf(handler.Requests[1]).Should().BeNull("a server may not pin a cookie for a host it is not");
        }
    }

    [Fact]
    public async Task An_empty_value_deletes_the_cookie()
    {
        var (client, handler) = NewClient();
        using (client)
        {
            handler
                .EnqueueWithCookies("session=abc", "other=keep")
                .EnqueueWithCookies("session=; Max-Age=0")
                .EnqueueOk();

            await SendAsync(client);
            await SendAsync(client);
            await SendAsync(client);

            CookieHeaderOf(handler.Requests[2]).Should().Be("other=keep");
        }
    }

    [Fact]
    public async Task A_set_cookie_without_a_name_value_pair_is_ignored()
    {
        var (client, handler) = NewClient();
        using (client)
        {
            handler.EnqueueWithCookies("garbage", "ok=1").EnqueueOk();

            await SendAsync(client);
            await SendAsync(client);

            CookieHeaderOf(handler.Requests[1]).Should().Be("ok=1");
        }
    }

    [Fact]
    public async Task Cookies_from_a_cross_origin_redirect_are_not_absorbed()
    {
        // HttpClient does not strip the Cookie and X-XSRF-TOKEN headers this client attaches by hand,
        // so a response that ended on a foreign host must not be allowed to write into the jar either.
        var (client, handler) = NewClient();
        using (client)
        {
            var redirected = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://evil.test/landing"),
            };
            redirected.Headers.TryAddWithoutValidation("Set-Cookie", "session=stolen");
            handler.Enqueue(redirected).EnqueueOk();

            await SendAsync(client);
            await SendAsync(client);

            CookieHeaderOf(handler.Requests[1]).Should().BeNull();
        }
    }

    [Fact]
    public async Task An_XSRF_cookie_primes_the_antiforgery_header_without_a_warmup()
    {
        var (client, handler) = NewClient();
        using (client)
        {
            handler.EnqueueWithCookies("XSRF-TOKEN=a%2Bb%3D").EnqueueOk();

            await SendAsync(client);
            await SendAsync(client, requiresAntiforgery: true);

            handler.Requests.Should().HaveCount(2, "a token already in the jar needs no warmup GET");
            handler.Requests[1].Headers.GetValues("X-XSRF-TOKEN").Should().Equal("a+b=");
        }
    }
}
