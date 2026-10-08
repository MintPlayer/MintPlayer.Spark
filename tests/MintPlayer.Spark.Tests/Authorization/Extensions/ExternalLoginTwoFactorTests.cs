using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.IdentityProvider;
using NSubstitute;
using Raven.Client.Documents;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// The host the #490 D11 tests boot: the real Spark auth endpoints over NSubstitute
/// <see cref="SignInManager{TUser}"/> / <see cref="UserManager{TUser}"/>, so the external cookie and
/// Identity's two-factor cookie are stood in for by what the stubs answer
/// (<c>GetExternalLoginInfoAsync</c>, <c>GetTwoFactorAuthenticationUserAsync</c>).
/// </summary>
internal sealed class ExternalTwoFactorTestHost
{
    public const string SignedInScheme = "TestSignedIn";

    public UserManager<SparkUser> UserManager { get; } = NewUserManagerStub();
    public SignInManager<SparkUser> SignInManager { get; }

    /// <summary>Answers every text key with the key itself (and the language for an explicit one), so pages can be asserted on keys.</summary>
    public IManager Manager { get; } = Substitute.For<IManager>();

    public ExternalTwoFactorTestHost()
    {
        SignInManager = NewSignInManagerStub(UserManager);
        Manager.GetTranslatedMessage(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => ci.ArgAt<string>(0));
        Manager.GetMessage(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => ci.ArgAt<string>(0) + "@" + ci.ArgAt<string>(1));
    }

    /// <summary>Signs every request in as <c>users/alice</c>, for the <c>/manage</c> endpoints.</summary>
    private sealed class AlwaysSignedInHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "users/alice")], SignedInScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SignedInScheme)));
        }
    }

    /// <param name="configuration">App configuration, e.g. <c>Spark:Auth:ExternalLogin:TwoFactor:Enabled</c>.</param>
    /// <param name="codeOptions">The code options (<see cref="SparkAuthenticationOptions.ExternalLoginTwoFactor"/>).</param>
    /// <param name="realAntiforgery">The framework's antiforgery instead of a permissive stub.</param>
    public async Task<TestServer> StartAsync(
        IDocumentStore store,
        IDictionary<string, string?>? configuration = null,
        Action<SparkExternalLoginTwoFactorOptions>? codeOptions = null,
        bool realAntiforgery = false)
    {
        var host = await new HostBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()))
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.Configure<SparkAuthenticationOptions>(o =>
                    {
                        o.LocalCredentials = SparkLocalCredentials.Full;
                        codeOptions?.Invoke(o.ExternalLoginTwoFactor);
                    });
                    services.AddTestMailSink();
                    services.AddHttpContextAccessor();
                    services.AddRouting();
                    services.AddAuthentication(SignedInScheme)
                        .AddScheme<AuthenticationSchemeOptions, AlwaysSignedInHandler>(SignedInScheme, _ => { });
                    services.AddAuthorizationBuilder()
                        .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(SignedInScheme)
                            .RequireAuthenticatedUser().Build());

                    // The two-factor page translates through IManager (D11 keeps the text service in Spark).
                    services.AddSingleton(Manager);

                    if (!realAntiforgery)
                        services.AddSingleton(PermissiveAntiforgery());

                    services.AddScoped(_ => SignInManager);
                    services.AddScoped(_ => UserManager);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseAntiforgery();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>());
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    public static ExternalLoginInfo NewLoginInfo(string name = "alice", IEnumerable<AuthenticationToken>? tokens = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, name + "@test.org"),
            new Claim(ClaimTypes.Name, name),
            new Claim("email_verified", "true"),
        ]));
        var info = new ExternalLoginInfo(principal, "TestProvider", "ext-key-" + name, "TestProvider");
        if (tokens is not null)
            info.AuthenticationTokens = tokens;
        return info;
    }

    private static IAntiforgery PermissiveAntiforgery()
    {
        var antiforgery = Substitute.For<IAntiforgery>();
        antiforgery.ValidateRequestAsync(Arg.Any<HttpContext>()).Returns(Task.CompletedTask);
        antiforgery.IsRequestValidAsync(Arg.Any<HttpContext>()).Returns(Task.FromResult(true));
        antiforgery.GetAndStoreTokens(Arg.Any<HttpContext>())
            .Returns(new AntiforgeryTokenSet("request", "cookie", "field", "header"));
        return antiforgery;
    }

    private static UserManager<SparkUser> NewUserManagerStub() => Substitute.For<UserManager<SparkUser>>(
        Substitute.For<IUserStore<SparkUser>>(),
        Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
        Substitute.For<IPasswordHasher<SparkUser>>(),
        Array.Empty<IUserValidator<SparkUser>>(),
        Array.Empty<IPasswordValidator<SparkUser>>(),
        Substitute.For<ILookupNormalizer>(),
        new IdentityErrorDescriber(),
        Substitute.For<IServiceProvider>(),
        Substitute.For<ILogger<UserManager<SparkUser>>>());

    private static SignInManager<SparkUser> NewSignInManagerStub(UserManager<SparkUser> userManager) => Substitute.For<SignInManager<SparkUser>>(
        userManager,
        Substitute.For<IHttpContextAccessor>(),
        Substitute.For<IUserClaimsPrincipalFactory<SparkUser>>(),
        Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
        Substitute.For<ILogger<SignInManager<SparkUser>>>(),
        Substitute.For<IAuthenticationSchemeProvider>(),
        Substitute.For<IUserConfirmation<SparkUser>>());
}

/// <summary>
/// #490 D11 — the application's own second factor after an external sign-in: the callback's redirect
/// to <c>/spark/auth/external-login/two-factor</c>, the page, its POST, and the switches
/// (<c>Spark:Auth:ExternalLogin:TwoFactor:{Enabled,AllowUserBypass}</c>).
/// </summary>
/// <remarks>
/// Neither the provider's external cookie nor Identity's two-factor cookie is minted here: both are
/// what <see cref="SignInManager{TUser}"/> reads, and the stubs answer for them. What is pinned is
/// everything the endpoints decide from those answers. The full popup round trip in a browser is E2E.
/// </remarks>
public class ExternalLoginTwoFactorTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string ValidNonce = "AbCdEfGhIjKlMnOpQrStUvWxYz012-_9";
    private const string PagePath = "/spark/auth/external-login/two-factor";

    private readonly ExternalTwoFactorTestHost host = new();
    private SignInManager<SparkUser> Sim => host.SignInManager;
    private UserManager<SparkUser> Um => host.UserManager;

    private static SparkUser TwoFactorUser(bool bypass = false) => new()
    {
        Id = "users/alice",
        UserName = "alice",
        TwoFactorEnabled = true,
        BypassTwoFactorForExternalLogin = bypass,
    };

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> QueryOf(string location)
        => QueryHelpers.ParseQuery(location.Contains('?') ? location[location.IndexOf('?')..] : "");

    private static string PathOf(string location) => location.Split('?', 2)[0];

    /// <summary>A linked external login whose sign-in Identity answers with <paramref name="whenNotBypassed"/> / <paramref name="whenBypassed"/>.</summary>
    private void ArrangeCallback(SparkUser linked, SignInResult whenNotBypassed, SignInResult? whenBypassed = null)
    {
        var info = ExternalTwoFactorTestHost.NewLoginInfo();
        Sim.GetExternalLoginInfoAsync().Returns(info);
        Um.FindByLoginAsync(info.LoginProvider, info.ProviderKey).Returns(linked);
        Sim.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, true, false).Returns(whenNotBypassed);
        Sim.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, true, true).Returns(whenBypassed ?? SignInResult.Success);
    }

    // ---------- the callback ----------

    [Fact]
    public async Task A_two_factor_account_is_sent_to_the_page_carrying_popup_nonce_and_returnUrl()
    {
        ArrangeCallback(TwoFactorUser(), SignInResult.TwoFactorRequired);
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().GetAsync(
            $"/spark/auth/external-login-callback?returnUrl=%2Fhome&popup=1&nonce={ValidNonce}&ngsw-bypass=true");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        PathOf(location).Should().Be(PagePath);
        var query = QueryOf(location);
        query["returnUrl"].ToString().Should().Be("/home");
        query["popup"].ToString().Should().Be("true");
        query["nonce"].ToString().Should().Be(ValidNonce);
        query["ngsw-bypass"].ToString().Should().Be("true", "a service worker must not answer the page from its cache (#464 D8)");

        await Sim.Received().ExternalLoginSignInAsync("TestProvider", "ext-key-alice", true, false);
        await Sim.DidNotReceive().SignInAsync(Arg.Any<SparkUser>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task The_redirect_to_the_page_carries_a_sanitized_returnUrl_and_drops_a_malformed_nonce()
    {
        ArrangeCallback(TwoFactorUser(), SignInResult.TwoFactorRequired);
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().GetAsync(
            $"/spark/auth/external-login-callback?returnUrl={Uri.EscapeDataString("https://evil.example/x")}&nonce=%3Cscript%3E");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryOf(response.Headers.Location!.OriginalString);
        query["returnUrl"].ToString().Should().Be("/", "an off-site return URL is an open redirect after sign-in");
        query.Should().NotContainKey("nonce");
        query.Should().NotContainKey("popup");
    }

    [Fact]
    public async Task With_the_step_switched_off_in_configuration_the_external_sign_in_alone_suffices()
    {
        ArrangeCallback(TwoFactorUser(), whenNotBypassed: SignInResult.TwoFactorRequired, whenBypassed: SignInResult.Success);
        using var server = await host.StartAsync(Store,
            configuration: new Dictionary<string, string?> { ["Spark:Auth:ExternalLogin:TwoFactor:Enabled"] = "false" });

        var response = await server.CreateClient().GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fhome");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/home");
        await Sim.Received().ExternalLoginSignInAsync("TestProvider", "ext-key-alice", true, true);
    }

    /// <summary>A user's own bypass counts only while the application allows bypassing.</summary>
    [Theory]
    [InlineData(false, PagePath)]
    [InlineData(true, "/home")]
    public async Task A_users_bypass_is_honoured_only_when_AllowUserBypass_is_on(bool allowUserBypass, string expectedPath)
    {
        ArrangeCallback(TwoFactorUser(bypass: true), whenNotBypassed: SignInResult.TwoFactorRequired, whenBypassed: SignInResult.Success);
        using var server = await host.StartAsync(Store, codeOptions: o => o.AllowUserBypass = allowUserBypass);

        var response = await server.CreateClient().GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fhome");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        PathOf(response.Headers.Location!.OriginalString).Should().Be(expectedPath);
        await Sim.Received().ExternalLoginSignInAsync("TestProvider", "ext-key-alice", true, allowUserBypass);
    }

    // ---------- the page ----------

    [Fact]
    public async Task The_page_without_the_two_factor_cookie_ends_the_popup_flow_with_requires_two_factor()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(Task.FromResult<SparkUser?>(null));
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().GetAsync($"{PagePath}?returnUrl=%2Fhome&popup=true&nonce={ValidNonce}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = ExternalLoginPopupPayload.Parse(await response.Content.ReadAsStringAsync());
        payload.Success.Should().BeFalse();
        payload.Error.Should().Be("requires_two_factor");
        payload.Nonce.Should().Be(ValidNonce);
    }

    [Fact]
    public async Task The_page_without_the_two_factor_cookie_redirects_with_requires_two_factor_outside_a_popup()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(Task.FromResult<SparkUser?>(null));
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().GetAsync($"{PagePath}?returnUrl=%2Fhome");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/home?sparkExternalLogin=requires_two_factor");
    }

    [Fact]
    public async Task The_page_renders_a_code_form_with_an_antiforgery_token_and_the_hand_off_flags()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        using var server = await host.StartAsync(Store, realAntiforgery: true);

        var response = await server.CreateClient().GetAsync($"{PagePath}?returnUrl=%2Fhome&popup=true&nonce={ValidNonce}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var html = await response.Content.ReadAsStringAsync();
        OidcTestHost.AntiforgeryTokenFrom(html).Should().NotBeNullOrEmpty();
        html.Should().Contain("<form method=\"post\">");
        html.Should().Contain("name=\"returnUrl\" value=\"/home\"");
        html.Should().Contain("name=\"popup\" value=\"true\"");
        html.Should().Contain($"name=\"nonce\" value=\"{ValidNonce}\"");
        html.Should().Contain("name=\"code\"");
        html.Should().Contain("name=\"rememberBrowser\"");
        html.Should().NotContain("name=\"recoveryCode\"");
        html.Should().NotContain("class=\"error\"");
    }

    [Fact]
    public async Task The_page_switches_to_a_recovery_code_field()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        using var server = await host.StartAsync(Store);

        var html = await (await server.CreateClient().GetAsync($"{PagePath}?returnUrl=%2Fhome&recovery=true")).Content.ReadAsStringAsync();

        html.Should().Contain("name=\"recoveryCode\"");
        html.Should().Contain("name=\"useRecoveryCode\" value=\"true\"");
        html.Should().NotContain("name=\"code\"");
    }

    [Fact]
    public async Task The_page_shows_a_fixed_message_for_a_known_error_and_never_echoes_the_query()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        using var server = await host.StartAsync(Store);
        var client = server.CreateClient();

        var known = await (await client.GetAsync($"{PagePath}?returnUrl=%2Fhome&error=invalid_code")).Content.ReadAsStringAsync();
        known.Should().Contain("<div class=\"error\">auth.externalTwoFactorInvalidCode</div>");

        var hostile = await (await client.GetAsync(
            $"{PagePath}?returnUrl=%2Fhome&error={Uri.EscapeDataString("<script>alert(1)</script>")}")).Content.ReadAsStringAsync();
        hostile.Should().NotContain("class=\"error\"");
        hostile.Should().NotContain("alert(1)");
    }

    [Fact]
    public async Task The_page_honours_the_spark_lang_cookie_when_it_is_a_culture_name()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        using var server = await host.StartAsync(Store);

        var request = new HttpRequestMessage(HttpMethod.Get, $"{PagePath}?returnUrl=%2Fhome");
        request.Headers.Add("Cookie", "spark-lang=nl");
        var html = await (await server.CreateClient().SendAsync(request)).Content.ReadAsStringAsync();

        html.Should().Contain("auth.externalTwoFactorTitle@nl");
    }

    // ---------- the POST ----------

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    [Fact]
    public async Task A_correct_code_in_a_popup_ends_with_the_hand_off_page_and_keeps_the_providers_tokens()
    {
        var user = TwoFactorUser();
        var info = ExternalTwoFactorTestHost.NewLoginInfo(tokens: [new AuthenticationToken { Name = "access_token", Value = "provider-at" }]);
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(user);
        Sim.TwoFactorAuthenticatorSignInAsync("123456", true, false).Returns(SignInResult.Success);
        Sim.GetExternalLoginInfoAsync().Returns(info);
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().PostAsync(PagePath, Form(
            ("code", "123 456"), ("returnUrl", "/home"), ("popup", "true"), ("nonce", ValidNonce)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = ExternalLoginPopupPayload.Parse(await response.Content.ReadAsStringAsync());
        payload.Type.Should().Be("spark:external-login");
        payload.Success.Should().BeTrue();
        payload.Error.Should().BeNull();
        payload.Nonce.Should().Be(ValidNonce);

        await Sim.Received().TwoFactorAuthenticatorSignInAsync("123456", true, false);
        await Um.Received().SetAuthenticationTokenAsync(user, "TestProvider", "access_token", "provider-at");
    }

    [Fact]
    public async Task A_correct_code_outside_a_popup_redirects_to_the_return_url_and_remembers_the_browser_when_asked()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        Sim.TwoFactorAuthenticatorSignInAsync("123456", true, true).Returns(SignInResult.Success);
        Sim.GetExternalLoginInfoAsync().Returns(Task.FromResult<ExternalLoginInfo?>(null));
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().PostAsync(PagePath, Form(
            ("code", "123456"), ("returnUrl", "/home"), ("rememberBrowser", "true")));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/home");
        await Sim.Received().TwoFactorAuthenticatorSignInAsync("123456", true, true);
    }

    [Fact]
    public async Task A_wrong_code_goes_back_to_the_page_with_an_error_and_keeps_the_flags()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        Sim.TwoFactorAuthenticatorSignInAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(SignInResult.Failed);
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().PostAsync(PagePath, Form(
            ("code", "000000"), ("returnUrl", "/home"), ("popup", "true"), ("nonce", ValidNonce)));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        PathOf(location).Should().Be(PagePath);
        var query = QueryOf(location);
        query["error"].ToString().Should().Be("invalid_code");
        query["returnUrl"].ToString().Should().Be("/home");
        query["popup"].ToString().Should().Be("true");
        query["nonce"].ToString().Should().Be(ValidNonce);
        query.Should().NotContainKey("recovery");
    }

    [Fact]
    public async Task An_empty_code_goes_back_with_missing_code_without_asking_Identity()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().PostAsync(PagePath, Form(("code", "  "), ("returnUrl", "/home")));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryOf(response.Headers.Location!.OriginalString)["error"].ToString().Should().Be("missing_code");
        await Sim.DidNotReceiveWithAnyArgs().TwoFactorAuthenticatorSignInAsync(default!, default, default);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_and_a_wrong_one_goes_back_to_the_recovery_form()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        Sim.TwoFactorRecoveryCodeSignInAsync(Arg.Any<string>()).Returns(SignInResult.Failed);
        Sim.TwoFactorRecoveryCodeSignInAsync("abcd1234").Returns(SignInResult.Success);
        Sim.GetExternalLoginInfoAsync().Returns(Task.FromResult<ExternalLoginInfo?>(null));
        using var server = await host.StartAsync(Store);
        var client = server.CreateClient();

        var ok = await client.PostAsync(PagePath, Form(
            ("useRecoveryCode", "true"), ("recoveryCode", "abcd 1234"), ("returnUrl", "/home")));
        ok.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ok.Headers.Location!.OriginalString.Should().Be("/home");

        var wrong = await client.PostAsync(PagePath, Form(
            ("useRecoveryCode", "true"), ("recoveryCode", "nope"), ("returnUrl", "/home")));
        wrong.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryOf(wrong.Headers.Location!.OriginalString);
        query["error"].ToString().Should().Be("invalid_recovery_code");
        query["recovery"].ToString().Should().Be("true", "the user stays on the form they were using");
    }

    [Fact]
    public async Task A_lockout_ends_the_flow_with_locked_out()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(TwoFactorUser());
        Sim.TwoFactorAuthenticatorSignInAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(SignInResult.LockedOut);
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().PostAsync(PagePath, Form(("code", "000000"), ("returnUrl", "/home")));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/home?sparkExternalLogin=locked_out");
    }

    [Fact]
    public async Task A_post_without_the_two_factor_cookie_ends_with_requires_two_factor_and_never_checks_the_code()
    {
        Sim.GetTwoFactorAuthenticationUserAsync().Returns(Task.FromResult<SparkUser?>(null));
        using var server = await host.StartAsync(Store);

        var response = await server.CreateClient().PostAsync(PagePath, Form(
            ("code", "123456"), ("returnUrl", "/home"), ("popup", "true"), ("nonce", ValidNonce)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = ExternalLoginPopupPayload.Parse(await response.Content.ReadAsStringAsync());
        payload.Success.Should().BeFalse();
        payload.Error.Should().Be("requires_two_factor");
        await Sim.DidNotReceiveWithAnyArgs().TwoFactorAuthenticatorSignInAsync(default!, default, default);
    }

    // ---------- pure pieces ----------

    /// <summary>Configuration wins over the code options when it holds a boolean; otherwise the code options stand.</summary>
    [Theory]
    [InlineData(null, null, false, true, false, true)]
    [InlineData("true", "false", false, true, true, false)]
    [InlineData("not-a-bool", "", false, true, false, true)]
    public void Resolve_prefers_configuration_over_code_options(
        string? configEnabled, string? configBypass, bool codeEnabled, bool codeBypass, bool expectEnabled, bool expectBypass)
    {
        var settings = new Dictionary<string, string?>();
        if (configEnabled is not null) settings["Spark:Auth:ExternalLogin:TwoFactor:Enabled"] = configEnabled;
        if (configBypass is not null) settings["Spark:Auth:ExternalLogin:TwoFactor:AllowUserBypass"] = configBypass;

        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<SparkAuthenticationOptions>(o =>
        {
            o.ExternalLoginTwoFactor.Enabled = codeEnabled;
            o.ExternalLoginTwoFactor.AllowUserBypass = codeBypass;
        });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();

        var resolved = ExternalLoginTwoFactor.Resolve(provider);

        resolved.Enabled.Should().Be(expectEnabled);
        resolved.AllowUserBypass.Should().Be(expectBypass);
    }

    [Fact]
    public void The_options_default_to_asking_and_to_no_user_bypass()
    {
        var defaults = new SparkExternalLoginTwoFactorOptions();
        defaults.Enabled.Should().BeTrue("a provider's sign-in does not prove possession of this account's authenticator");
        defaults.AllowUserBypass.Should().BeFalse();
    }

    [Fact]
    public void Url_carries_every_flag_under_the_path_base()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/app";

        var url = ExternalLoginTwoFactor.Url(context.Request.PathBase, popup: true, nonce: ValidNonce, returnUrl: "/home?a=1&b=2", error: "invalid_code", recovery: true);

        PathOf(url).Should().Be("/app" + PagePath);
        var query = QueryOf(url);
        query["returnUrl"].ToString().Should().Be("/home?a=1&b=2", "the return URL is encoded, not spliced");
        query["popup"].ToString().Should().Be("true");
        query["nonce"].ToString().Should().Be(ValidNonce);
        query["error"].ToString().Should().Be("invalid_code");
        query["recovery"].ToString().Should().Be("true");
        query["ngsw-bypass"].ToString().Should().Be("true");
    }

    [Fact]
    public void Url_leaves_out_the_flags_that_are_not_set()
    {
        var url = ExternalLoginTwoFactor.Url(new DefaultHttpContext().Request.PathBase, popup: false, nonce: null, returnUrl: "/");

        var query = QueryOf(url);
        query.Keys.ToList().Should().BeEquivalentTo(["returnUrl", "ngsw-bypass"]);
    }
}
