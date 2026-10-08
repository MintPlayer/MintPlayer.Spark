using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Facebook;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// #490 M1, D4 — a failure at the provider hop (the user cancels, the correlation fails) is reported
/// the way every other outcome is, instead of as an unhandled exception inside the popup.
/// </summary>
public class SparkExternalLoginRemoteFailureTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string ValidNonce = "AbCdEfGhIjKlMnOpQrStUvWxYz012-_9";
    private const string AccessDeniedMessage = "Access was denied by the resource owner or by the remote server.";

    private sealed record Written(int StatusCode, string? Location, string Body, bool Handled);

    /// <summary>Runs the handler against a bare context, the way the remote handler would call it.</summary>
    private static async Task<Written> HandleAsync(string? redirectUri, string failureMessage, string query = "")
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        http.Request.QueryString = new QueryString(query);
        var body = new MemoryStream();
        http.Response.Body = body;

        var context = new RemoteFailureContext(
            http,
            new AuthenticationScheme("GitHub", "GitHub", typeof(OAuthHandler<OAuthOptions>)),
            new OAuthOptions(),
            new AuthenticationFailureException(failureMessage))
        {
            Properties = redirectUri is null ? null : new AuthenticationProperties { RedirectUri = redirectUri },
        };

        await SparkExternalLoginRemoteFailure.Handle(context);

        return new Written(
            http.Response.StatusCode,
            http.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
            System.Text.Encoding.UTF8.GetString(body.ToArray()),
            context.Result?.Handled == true);
    }

    private static string PopupCallback(string returnUrl = "/home")
        => $"/spark/auth/external-login-callback?returnUrl={Uri.EscapeDataString(returnUrl)}&popup=1&nonce={ValidNonce}&ngsw-bypass=true";

    // --- the handler --------------------------------------------------------

    [Fact]
    public async Task A_provider_failure_in_popup_mode_serves_the_hand_off_page_with_remote_failure()
    {
        var written = await HandleAsync(PopupCallback(), "Correlation failed.");

        written.Handled.Should().BeTrue();
        written.StatusCode.Should().Be(StatusCodes.Status200OK);
        written.Body.Should().Contain($$"""{"type":"spark:external-login","success":false,"error":"remote_failure","nonce":"{{ValidNonce}}"}""");
        written.Body.Should().Contain("var returnUrl = \"/home\";");
    }

    [Fact]
    public async Task A_user_who_declines_at_the_provider_is_reported_as_no_login_info()
    {
        var written = await HandleAsync(PopupCallback(), AccessDeniedMessage, "?error=access_denied&state=x");

        written.Handled.Should().BeTrue();
        written.Body.Should().Contain("\"error\":\"no_login_info\"");
    }

    [Fact]
    public async Task An_access_denied_query_counts_when_the_handler_words_the_failure_differently()
    {
        var written = await HandleAsync(PopupCallback(), "The user cancelled.", "?error=access_denied");

        written.Body.Should().Contain("\"error\":\"no_login_info\"");
    }

    [Fact]
    public async Task A_correlation_failure_stays_remote_failure_whatever_the_query_says()
    {
        var written = await HandleAsync(PopupCallback(), "Correlation failed.", "?error=access_denied");

        written.Body.Should().Contain("\"error\":\"remote_failure\"");
    }

    [Fact]
    public async Task A_provider_failure_in_redirect_mode_redirects_with_the_code()
    {
        var written = await HandleAsync("/spark/auth/external-login-callback?returnUrl=%2Fdashboard&ngsw-bypass=true", "Correlation failed.");

        written.Handled.Should().BeTrue();
        written.StatusCode.Should().Be(StatusCodes.Status302Found);
        written.Location.Should().Be("/dashboard?sparkExternalLogin=remote_failure");
    }

    [Fact]
    public async Task A_hostile_return_url_in_the_redirect_uri_is_sanitized()
    {
        var written = await HandleAsync(
            $"/spark/auth/external-login-callback?returnUrl={Uri.EscapeDataString("https://evil.example/")}",
            "Correlation failed.");

        written.Location.Should().Be("/?sparkExternalLogin=remote_failure");
    }

    [Fact]
    public async Task Without_a_redirect_uri_the_failure_redirects_to_the_root_with_the_code()
    {
        var written = await HandleAsync(redirectUri: null, "The oauth state was missing or invalid.");

        written.Handled.Should().BeTrue();
        written.StatusCode.Should().Be(StatusCodes.Status302Found);
        written.Location.Should().Be("/?sparkExternalLogin=remote_failure");
    }

    [Fact]
    public async Task A_malformed_nonce_in_the_redirect_uri_is_dropped()
    {
        var written = await HandleAsync(
            $"/spark/auth/external-login-callback?returnUrl=%2Fhome&popup=1&nonce={Uri.EscapeDataString("x'</script>")}",
            "Correlation failed.");

        written.Body.Should().Contain("\"nonce\":null");
        written.Body.Should().NotContain("x'");
    }

    // --- the presets --------------------------------------------------------

    private static RemoteAuthenticationEvents EventsOf<TOptions>(Action<MintPlayer.Spark.Abstractions.Builder.ISparkBuilder> register, string scheme)
        where TOptions : RemoteAuthenticationOptions
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        register(TestSparkAuth.Builder(services));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<TOptions>>().Get(scheme).Events;
    }

    private static bool IsSparkDefault(Func<RemoteFailureContext, Task> handler)
        => handler.Method.Equals(typeof(SparkExternalLoginRemoteFailure).GetMethod(nameof(SparkExternalLoginRemoteFailure.Handle)));

    [Fact]
    public void Every_preset_reports_remote_failures_by_default()
    {
        IsSparkDefault(EventsOf<OAuthOptions>(b => b.AddGitHub(o => { o.ClientId = "id"; o.ClientSecret = "secret"; }), "GitHub").OnRemoteFailure)
            .Should().BeTrue("GitHub");
        IsSparkDefault(EventsOf<GoogleOptions>(b => b.AddGoogle(o => { o.ClientId = "id"; o.ClientSecret = "secret"; }), GoogleDefaults.AuthenticationScheme).OnRemoteFailure)
            .Should().BeTrue("Google");
        IsSparkDefault(EventsOf<MicrosoftAccountOptions>(b => b.AddMicrosoftAccount(o => { o.ClientId = "id"; o.ClientSecret = "secret"; }), MicrosoftAccountDefaults.AuthenticationScheme).OnRemoteFailure)
            .Should().BeTrue("Microsoft");
        IsSparkDefault(EventsOf<FacebookOptions>(b => b.AddFacebook(o => { o.AppId = "id"; o.AppSecret = "secret"; }), FacebookDefaults.AuthenticationScheme).OnRemoteFailure)
            .Should().BeTrue("Facebook");
        IsSparkDefault(EventsOf<OAuthOptions>(b => b.AddTwitter(o => { o.ClientId = "id"; o.ClientSecret = "secret"; }), "Twitter").OnRemoteFailure)
            .Should().BeTrue("Twitter");
        IsSparkDefault(EventsOf<OAuthOptions>(b => b.AddLinkedIn(o => { o.ClientId = "id"; o.ClientSecret = "secret"; }), "LinkedIn").OnRemoteFailure)
            .Should().BeTrue("LinkedIn");
        IsSparkDefault(EventsOf<OpenIdConnectOptions>(b => b.AddOpenIdConnect("Idp", "Our IdP", o => { o.Authority = "https://idp.test"; o.ClientId = "id"; }), "Idp").OnRemoteFailure)
            .Should().BeTrue("OpenID Connect");
    }

    [Fact]
    public void An_application_can_still_override_the_preset_remote_failure_handler()
    {
        Func<RemoteFailureContext, Task> appHandler = _ => Task.CompletedTask;

        var events = EventsOf<OAuthOptions>(b => b.AddGitHub(o =>
        {
            o.ClientId = "id";
            o.ClientSecret = "secret";
            o.Events.OnRemoteFailure = appHandler;
        }), "GitHub");

        events.OnRemoteFailure.Should().BeSameAs(appHandler);
    }

    // --- through the real OAuth handler ----------------------------------------

    private async Task<IHost> StartGitHubHostAsync()
        => await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    TestSparkAuth.Builder(services).AddGitHub(o =>
                    {
                        o.ClientId = "test-client-id";
                        o.ClientSecret = "test-client-secret";
                    });
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>());
                }))
            .StartAsync();

    /// <summary>Starts a GitHub challenge and returns its OAuth state and correlation cookie.</summary>
    private static async Task<(string State, string CorrelationCookie)> ChallengeAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/spark/auth/external-login?provider=GitHub&{query}");
        response.StatusCode.Should().Be(HttpStatusCode.Found);

        var state = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
        return (state, cookie[..cookie.IndexOf(';')]);
    }

    [Fact]
    public async Task A_cancel_at_github_reaches_the_popup_as_no_login_info()
    {
        using var host = await StartGitHubHostAsync();
        using var client = host.GetTestServer().CreateClient();
        var (state, correlation) = await ChallengeAsync(client, $"returnUrl=%2Fdashboard&popup=1&nonce={ValidNonce}");

        using var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/signin-github?error=access_denied&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", correlation);
        var response = await client.SendAsync(callback);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain($$"""{"type":"spark:external-login","success":false,"error":"no_login_info","nonce":"{{ValidNonce}}"}""");
        body.Should().Contain("var returnUrl = \"/dashboard\";");
    }

    [Fact]
    public async Task A_failed_correlation_in_redirect_mode_returns_to_the_app_with_remote_failure()
    {
        using var host = await StartGitHubHostAsync();
        using var client = host.GetTestServer().CreateClient();
        var (state, _) = await ChallengeAsync(client, "returnUrl=%2Fdashboard");

        // No correlation cookie: the round trip cannot be tied to this browser.
        var response = await client.GetAsync($"/signin-github?code=abc&state={Uri.EscapeDataString(state)}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/dashboard?sparkExternalLogin=remote_failure");
    }
}
