using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.Client.Tests._Infrastructure;

namespace MintPlayer.Spark.Client.Tests.Authorization;

/// <summary>
/// Pins the wire shape of the four <c>SparkClient</c> auth extension methods. These map
/// directly onto identity endpoints the server exposes, so a regression here breaks every
/// downstream app that registers, logs in, or reads the current user via the SDK.
/// </summary>
public class SparkClientAuthExtensionsTests
{
    private static SparkClient NewClient(ScriptedHttpHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, ownsClient: true);

    private static StringContent JsonOk(string body) =>
        new(body, System.Text.Encoding.UTF8, "application/json");

    private static HttpResponseMessage MeResponse(bool authenticated = true) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonOk($$"""
                {
                  "isAuthenticated": {{(authenticated ? "true" : "false")}},
                  "userName": "alice",
                  "email": "alice@example.com",
                  "roles": ["admin"]
                }
                """)
        };

    #region LoginAsync

    [Fact]
    public async Task LoginAsync_posts_email_and_password_to_login_endpoint_without_antiforgery()
    {
        var handler = new ScriptedHttpHandler()
            .Enqueue(new HttpResponseMessage(HttpStatusCode.OK))   // login
            .Enqueue(MeResponse());                                // /me
        using var client = NewClient(handler);

        await client.LoginAsync("alice@example.com", "p@ss");

        handler.Requests.Should().HaveCount(2);

        var login = handler.Requests[0];
        login.Method.Should().Be(HttpMethod.Post);
        login.RequestUri!.PathAndQuery.Should().Be("/spark/auth/login?useCookies=true");
        // The login endpoint sits outside Spark's antiforgery surface — no warmup, no header.
        login.Headers.Contains("X-XSRF-TOKEN").Should().BeFalse();
    }

    [Fact]
    public async Task LoginAsync_calls_me_after_successful_login_to_re_prime_antiforgery()
    {
        var handler = new ScriptedHttpHandler()
            .Enqueue(new HttpResponseMessage(HttpStatusCode.OK))
            .Enqueue(MeResponse());
        using var client = NewClient(handler);

        await client.LoginAsync("alice@example.com", "p@ss");

        handler.Requests[1].Method.Should().Be(HttpMethod.Get);
        handler.Requests[1].RequestUri!.AbsolutePath.Should().Be("/spark/auth/me");
    }

    [Fact]
    public async Task LoginAsync_throws_SparkClientException_when_server_returns_non_success()
    {
        var handler = new ScriptedHttpHandler()
            .Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = NewClient(handler);

        var act = async () => await client.LoginAsync("alice@example.com", "wrong");

        var ex = await act.Should().ThrowAsync<SparkClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LoginAsync_throws_ArgumentNullException_when_client_is_null()
    {
        var act = async () => await SparkClientAuthExtensions.LoginAsync(null!, "x", "y");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region RegisterAsync

    [Fact]
    public async Task RegisterAsync_posts_email_and_password_to_register_endpoint_without_antiforgery()
    {
        var handler = new ScriptedHttpHandler().EnqueueOk();
        using var client = NewClient(handler);

        await client.RegisterAsync("alice@example.com", "p@ss");

        handler.Requests.Should().ContainSingle();

        var register = handler.Requests[0];
        register.Method.Should().Be(HttpMethod.Post);
        register.RequestUri!.AbsolutePath.Should().Be("/spark/auth/register");
        register.Headers.Contains("X-XSRF-TOKEN").Should().BeFalse();
    }

    [Fact]
    public async Task RegisterAsync_throws_SparkClientException_on_non_success()
    {
        var handler = new ScriptedHttpHandler()
            .Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client = NewClient(handler);

        var act = async () => await client.RegisterAsync("alice@example.com", "weak");

        await act.Should().ThrowAsync<SparkClientException>();
    }

    [Fact]
    public async Task RegisterAsync_throws_ArgumentNullException_when_client_is_null()
    {
        var act = async () => await SparkClientAuthExtensions.RegisterAsync(null!, "x", "y");

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region LogoutAsync

    [Fact]
    public async Task LogoutAsync_attaches_antiforgery_token_via_warmup_then_posts_to_logout()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(  // warmup
                ".AspNetCore.Antiforgery.abc=validation; Path=/",
                "XSRF-TOKEN=t-token; Path=/")
            .EnqueueOk();         // logout
        using var client = NewClient(handler);

        await client.LogoutAsync();

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().EndWith("__warmup__");

        var logout = handler.Requests[1];
        logout.Method.Should().Be(HttpMethod.Post);
        logout.RequestUri!.AbsolutePath.Should().Be("/spark/auth/logout");
        logout.Headers.GetValues("X-XSRF-TOKEN").Should().ContainSingle().Which.Should().Be("t-token");
    }

    [Fact]
    public async Task LogoutAsync_invalidates_antiforgery_so_next_mutation_re_warms_up()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(  // warmup #1
                ".AspNetCore.Antiforgery.abc=validation; Path=/",
                "XSRF-TOKEN=t-1; Path=/")
            .EnqueueOk()          // logout
            .EnqueueWithCookies(  // warmup #2 — proves the cached token was dropped
                ".AspNetCore.Antiforgery.abc=validation; Path=/",
                "XSRF-TOKEN=t-2; Path=/")
            .EnqueueOk();         // some subsequent mutating call
        using var client = NewClient(handler);

        await client.LogoutAsync();
        await client.DeletePersistentObjectAsync(Guid.NewGuid(), "id");

        // 1: warmup, 2: logout, 3: warmup again, 4: delete
        handler.Requests.Should().HaveCount(4);
        handler.Requests[2].RequestUri!.AbsolutePath.Should().EndWith("__warmup__");
        handler.Requests[3].Headers.GetValues("X-XSRF-TOKEN").Should().ContainSingle().Which.Should().Be("t-2");
    }

    [Fact]
    public async Task LogoutAsync_throws_SparkClientException_on_non_success()
    {
        var handler = new ScriptedHttpHandler()
            .EnqueueWithCookies(
                ".AspNetCore.Antiforgery.abc=validation; Path=/",
                "XSRF-TOKEN=t; Path=/")
            .Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = NewClient(handler);

        var act = async () => await client.LogoutAsync();

        await act.Should().ThrowAsync<SparkClientException>();
    }

    [Fact]
    public async Task LogoutAsync_throws_ArgumentNullException_when_client_is_null()
    {
        var act = async () => await SparkClientAuthExtensions.LogoutAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region GetCurrentUserAsync

    [Fact]
    public async Task GetCurrentUserAsync_gets_me_endpoint_and_deserializes_payload()
    {
        var handler = new ScriptedHttpHandler().Enqueue(MeResponse());
        using var client = NewClient(handler);

        var info = await client.GetCurrentUserAsync();

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/spark/auth/me");

        info.IsAuthenticated.Should().BeTrue();
        info.UserName.Should().Be("alice");
        info.Email.Should().Be("alice@example.com");
        info.Roles.Should().ContainSingle().Which.Should().Be("admin");
    }

    [Fact]
    public async Task GetCurrentUserAsync_throws_SparkClientException_when_response_body_is_empty()
    {
        var handler = new ScriptedHttpHandler()
            .Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonOk("null")
            });
        using var client = NewClient(handler);

        var act = async () => await client.GetCurrentUserAsync();

        await act.Should().ThrowAsync<SparkClientException>();
    }

    [Fact]
    public async Task GetCurrentUserAsync_throws_SparkClientException_on_non_success()
    {
        var handler = new ScriptedHttpHandler()
            .Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = NewClient(handler);

        var act = async () => await client.GetCurrentUserAsync();

        var ex = await act.Should().ThrowAsync<SparkClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCurrentUserAsync_throws_ArgumentNullException_when_client_is_null()
    {
        var act = async () => await SparkClientAuthExtensions.GetCurrentUserAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region SparkUserInfo

    [Fact]
    public void SparkUserInfo_deserializes_authenticated_payload()
    {
        var json = """
            { "isAuthenticated": true, "userName": "alice", "email": "alice@example.com", "roles": ["admin", "editor"] }
            """;

        var info = JsonSerializer.Deserialize<SparkUserInfo>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        info.Should().NotBeNull();
        info!.IsAuthenticated.Should().BeTrue();
        info.UserName.Should().Be("alice");
        info.Roles.Should().BeEquivalentTo(["admin", "editor"]);
    }

    [Fact]
    public void SparkUserInfo_deserializes_anonymous_payload_with_only_isAuthenticated()
    {
        var json = """{ "isAuthenticated": false }""";

        var info = JsonSerializer.Deserialize<SparkUserInfo>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        info.Should().NotBeNull();
        info!.IsAuthenticated.Should().BeFalse();
        info.UserName.Should().BeNull();
        info.Email.Should().BeNull();
        info.Roles.Should().BeEmpty();
    }

    [Fact]
    public void SparkUserInfo_default_Roles_is_empty_array_not_null()
    {
        var info = new SparkUserInfo();

        info.Roles.Should().NotBeNull().And.BeEmpty();
    }

    #endregion
}
