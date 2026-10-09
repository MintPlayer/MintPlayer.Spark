using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// #464/#490 M4: the presets on <see cref="ISparkBuilder"/>, the <c>Spark:Auth:Providers</c> section,
/// the undeclared-remote-scheme guard, X on OAuth 2.0 and the OpenID Connect preset.
/// </summary>
public class ExternalProviderRegistrationTests
{
    private static (IServiceCollection Services, ISparkBuilder Spark) NewSpark(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSparkAuthentication<SparkUser>();
        return (services, TestSparkAuth.Builder(services, configuration));
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static async Task<AuthenticationScheme[]> RemoteSchemesAsync(IServiceProvider provider)
        => [.. (await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync())
            .Where(scheme => SparkExternalSchemeGuard.IsRemote(scheme.HandlerType))];

    // --- Spark:Auth:Providers ---------------------------------------------------------------------

    [Fact]
    public async Task Configuration_registers_exactly_the_configured_schemes()
    {
        var configuration = Config(
            ("Spark:Auth:Providers:GitHub:ClientId", "gh-id"),
            ("Spark:Auth:Providers:GitHub:ClientSecret", "gh-secret"),
            ("Spark:Auth:Providers:Twitter:ClientId", "x-id"),
            ("Spark:Auth:Providers:Twitter:ClientSecret", "x-secret"));
        var (services, spark) = NewSpark(configuration);

        spark.AddExternalProviders(configuration);

        await using var provider = services.BuildServiceProvider();
        (await RemoteSchemesAsync(provider)).Select(s => s.Name).Should().BeEquivalentTo(["GitHub", "Twitter"]);

        var gitHub = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("GitHub");
        gitHub.ClientId.Should().Be("gh-id");
        gitHub.ClientSecret.Should().Be("gh-secret");
        gitHub.CallbackPath.ToString().Should().Be("/signin-github", "the preset's defaults still apply");

        provider.GetServices<SparkExternalProviderRegistration>().Select(r => r.Scheme)
            .Should().BeEquivalentTo(["GitHub", "Twitter"]);
    }

    [Fact]
    public async Task A_provider_with_an_empty_ClientId_is_not_registered()
    {
        var configuration = Config(
            ("Spark:Auth:Providers:GitHub:ClientId", ""),
            ("Spark:Auth:Providers:GitHub:ClientSecret", "gh-secret"),
            ("Spark:Auth:Providers:Google:ClientSecret", "no-id"),
            ("Spark:Auth:Providers:OpenIdConnect:Idp:Authority", "https://idp.test"),
            ("Spark:Auth:Providers:LinkedIn:ClientId", "li-id"));
        var (services, spark) = NewSpark(configuration);

        spark.AddExternalProviders(configuration);

        await using var provider = services.BuildServiceProvider();
        (await RemoteSchemesAsync(provider)).Select(s => s.Name).Should().BeEquivalentTo(["LinkedIn"]);
    }

    [Fact]
    public void An_unknown_provider_key_throws_and_names_the_valid_keys()
    {
        var configuration = Config(("Spark:Auth:Providers:Gihtub:ClientId", "id"));
        var (_, spark) = NewSpark(configuration);

        var act = () => spark.AddExternalProviders(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'Gihtub'*GitHub, Google, MicrosoftAccount, Facebook, Twitter, LinkedIn*OpenIdConnect*");
    }

    [Fact]
    public async Task An_OpenIdConnect_section_registers_the_scheme_with_its_display_name()
    {
        var configuration = Config(
            ("Spark:Auth:Providers:OpenIdConnect:MintPlayer:DisplayName", "MintPlayer ID"),
            ("Spark:Auth:Providers:OpenIdConnect:MintPlayer:Authority", "https://idp.test"),
            ("Spark:Auth:Providers:OpenIdConnect:MintPlayer:ClientId", "rp"),
            ("Spark:Auth:Providers:OpenIdConnect:MintPlayer:ClientSecret", "rp-secret"));
        var (services, spark) = NewSpark(configuration);

        spark.AddExternalProviders(configuration, hooks => hooks.OpenIdConnect("MintPlayer", o => o.Scope.Add("roles")));

        await using var provider = services.BuildServiceProvider();
        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("MintPlayer");
        scheme.Should().NotBeNull();
        scheme!.DisplayName.Should().Be("MintPlayer ID");

        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("MintPlayer");
        options.Authority.Should().Be("https://idp.test");
        options.ClientId.Should().Be("rp");
        options.ClientSecret.Should().Be("rp-secret");
        options.ResponseType.Should().Be("code");
        options.ResponseMode.Should().Be("query", "the Spark IdP never does form_post (S6)");
        options.UsePkce.Should().BeTrue();
        options.SaveTokens.Should().BeFalse();
        options.GetClaimsFromUserInfoEndpoint.Should().BeTrue();
        options.MapInboundClaims.Should().BeFalse();
        // The hook runs after the defaults, so its scope is added to them.
        options.Scope.Should().BeEquivalentTo(["openid", "profile", "email", "roles"]);
        options.CallbackPath.ToString().Should().Be("/signin-MintPlayer");
        options.SignedOutCallbackPath.ToString().Should().Be("/signout-callback-MintPlayer");
        options.SignInScheme.Should().Be(Microsoft.AspNetCore.Identity.IdentityConstants.ExternalScheme);
    }

    [Fact]
    public void Configuration_then_code_merges_into_one_scheme()
    {
        var configuration = Config(
            ("Spark:Auth:Providers:GitHub:ClientId", "gh-id"),
            ("Spark:Auth:Providers:GitHub:ClientSecret", "gh-secret"));
        var (services, spark) = NewSpark(configuration);

        spark.AddExternalProviders(configuration);
        spark.AddGitHub(o => o.Scope.Add("read:org"));

        using var provider = services.BuildServiceProvider();
        var gitHub = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("GitHub");
        gitHub.ClientId.Should().Be("gh-id");
        gitHub.Scope.Should().Contain("read:org");
        provider.GetServices<SparkExternalProviderRegistration>().Should().ContainSingle();
    }

    [Fact]
    public void Two_different_presets_on_one_scheme_throw()
    {
        var (_, spark) = NewSpark();
        spark.AddGitHub("Forge", displayName: null);

        var act = () => spark.AddLinkedIn("Forge", displayName: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddLinkedIn*AddGitHub*");
    }

    // --- AddAuthentication first ------------------------------------------------------------------

    public static TheoryData<string> Presets => ["GitHub", "Google", "MicrosoftAccount", "Facebook", "Twitter", "LinkedIn", "OpenIdConnect", "Providers", "Scheme"];

    [Theory]
    [MemberData(nameof(Presets))]
    public void A_preset_before_AddAuthentication_throws(string preset)
    {
        var spark = new SparkBuilder(new ServiceCollection());

        Action act = preset switch
        {
            "GitHub" => () => spark.AddGitHub(),
            "Google" => () => spark.AddGoogle(),
            "MicrosoftAccount" => () => spark.AddMicrosoftAccount(),
            "Facebook" => () => spark.AddFacebook(),
            "Twitter" => () => spark.AddTwitter(),
            "LinkedIn" => () => spark.AddLinkedIn(),
            "OpenIdConnect" => () => spark.AddOpenIdConnect("Idp", "IdP"),
            "Providers" => () => spark.AddExternalProviders(Config()),
            _ => () => spark.AddExternalScheme("Raw", SparkExternalProviderPolicy.Default),
        };

        act.Should().Throw<InvalidOperationException>().WithMessage("*before spark.AddAuthentication<TUser>()*");
    }

    // --- the undeclared-scheme guard --------------------------------------------------------------

    private static void AddRawOAuth(IServiceCollection services)
        => services.AddAuthentication().AddOAuth("Raw", "Raw provider", o =>
        {
            o.ClientId = "id";
            o.ClientSecret = "secret";
            o.AuthorizationEndpoint = "https://raw.test/authorize";
            o.TokenEndpoint = "https://raw.test/token";
            o.CallbackPath = "/signin-raw";
        });

    [Fact]
    public void An_undeclared_remote_scheme_throws_at_startup()
    {
        var (services, _) = NewSpark();
        AddRawOAuth(services);
        services.AddAuthentication().AddCookie("Cookieish", "Not remote", _ => { });

        using var provider = services.BuildServiceProvider();
        var act = () => SparkExternalSchemeGuard.GuardAgainstUndeclaredRemoteSchemes(provider);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'Raw'*spark.AddExternalScheme*");
    }

    [Fact]
    public void AddExternalScheme_declares_a_raw_handler()
    {
        var (services, spark) = NewSpark();
        AddRawOAuth(services);
        spark.AddExternalScheme("Raw", SparkExternalProviderPolicy.WithoutVerifiedEmailSignal());

        using var provider = services.BuildServiceProvider();
        var act = () => SparkExternalSchemeGuard.GuardAgainstUndeclaredRemoteSchemes(provider);

        act.Should().NotThrow();
    }

    [Fact]
    public void Every_preset_counts_as_declared()
    {
        var (services, spark) = NewSpark();
        spark.AddGitHub(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        spark.AddGoogle(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        spark.AddMicrosoftAccount(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        spark.AddFacebook(o => { o.AppId = "id"; o.AppSecret = "secret"; });
        spark.AddTwitter(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        spark.AddLinkedIn(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        spark.AddOpenIdConnect("Idp", "IdP", o => { o.Authority = "https://idp.test"; o.ClientId = "id"; });

        using var provider = services.BuildServiceProvider();
        var act = () => SparkExternalSchemeGuard.GuardAgainstUndeclaredRemoteSchemes(provider);

        act.Should().NotThrow();
    }

    // --- X on OAuth 2.0 ---------------------------------------------------------------------------

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task X_token_request_carries_Basic_auth_and_no_client_secret_in_the_body()
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(new SparkXClientAuthenticationHandler(inner));

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.com/2/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = "my client",
                ["client_secret"] = "s3cr:t+",
                ["code"] = "the-code",
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = "verifier",
            }),
        };
        await client.SendAsync(request);

        inner.Request!.Headers.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(inner.Request.Headers.Authorization.Parameter!))
            .Should().Be("my%20client:s3cr%3At%2B");

        var body = QueryHelpers.ParseQuery(inner.Body);
        body.ContainsKey("client_secret").Should().BeFalse();
        body["client_id"].ToString().Should().Be("my client");
        body["code"].ToString().Should().Be("the-code");
        body["code_verifier"].ToString().Should().Be("verifier");
        body["grant_type"].ToString().Should().Be("authorization_code");
    }

    [Fact]
    public async Task X_other_requests_pass_through_untouched()
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(new SparkXClientAuthenticationHandler(inner));

        await client.GetAsync("https://api.x.com/2/users/me");

        inner.Request!.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task X_preset_defaults_and_userinfo_mapping()
    {
        var (services, spark) = NewSpark();
        spark.AddTwitter(o => { o.ClientId = "id"; o.ClientSecret = "secret"; });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("Twitter");

        options.AuthorizationEndpoint.Should().Be("https://x.com/i/oauth2/authorize");
        options.TokenEndpoint.Should().Be("https://api.x.com/2/oauth2/token");
        options.UsePkce.Should().BeTrue();
        options.Scope.Should().BeEquivalentTo(["users.read", "tweet.read", "users.email"]);
        options.CallbackPath.ToString().Should().Be("/signin-twitter");
        options.BackchannelHttpHandler.Should().BeOfType<SparkXClientAuthenticationHandler>();
        (await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("Twitter"))!.DisplayName.Should().Be("X");

        var backchannel = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{ "data": { "id": "2244994945", "name": "Jane Doe", "username": "jane", "confirmed_email": "jane@x.test" } }""",
                Encoding.UTF8, "application/json"),
        });
        var identity = new ClaimsIdentity("Twitter");
        using var tokens = JsonDocument.Parse("""{ "access_token": "access-token", "token_type": "bearer" }""");
        using var empty = JsonDocument.Parse("{}");
        var context = new OAuthCreatingTicketContext(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties(),
            new DefaultHttpContext { RequestServices = provider },
            new AuthenticationScheme("Twitter", "X", typeof(OAuthHandler<OAuthOptions>)),
            options,
            new HttpClient(backchannel),
            OAuthTokenResponse.Success(tokens),
            empty.RootElement);

        await options.Events.OnCreatingTicket(context);

        backchannel.Request!.RequestUri!.AbsoluteUri.Should().Be("https://api.x.com/2/users/me?user.fields=confirmed_email,name,username");
        backchannel.Request.Headers.Authorization!.Parameter.Should().Be("access-token");
        identity.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("2244994945");
        identity.FindFirst(ClaimTypes.Name)!.Value.Should().Be("Jane Doe");
        identity.FindFirst(ClaimTypes.Email)!.Value.Should().Be("jane@x.test");

        var policy = SparkExternalProviderPolicies.For(
            new SparkAuthenticationOptions(), provider.GetServices<SparkExternalProviderRegistration>(), "Twitter");
        policy.EmailVerification(new ClaimsPrincipal(identity)).Should().Be(SparkEmailVerification.NoSignal);
    }

    // --- OpenID Connect -------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "sub": "u1", "email": "a@idp.test", "name": "Ann", "email_verified": true }""", SparkEmailVerification.Verified)]
    [InlineData("""{ "sub": "u1", "email": "a@idp.test", "name": "Ann", "email_verified": "true" }""", SparkEmailVerification.Verified)]
    [InlineData("""{ "sub": "u1", "email": "a@idp.test", "name": "Ann", "email_verified": false }""", SparkEmailVerification.Unverified)]
    [InlineData("""{ "sub": "u1", "email": "a@idp.test", "name": "Ann" }""", SparkEmailVerification.Unverified)]
    public void OpenIdConnect_userinfo_email_verified_bool_and_string_both_verify(string userInfo, SparkEmailVerification expected)
    {
        var (services, spark) = NewSpark();
        spark.AddOpenIdConnect("Idp", "IdP", o => { o.Authority = "https://idp.test"; o.ClientId = "id"; });
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("Idp");

        var identity = new ClaimsIdentity("Idp");
        using var json = JsonDocument.Parse(userInfo);
        foreach (var action in options.ClaimActions)
            action.Run(json.RootElement, identity, "https://idp.test");
        SparkExternalProviderExtensions.MapOpenIdConnectClaims(identity);

        identity.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("u1");
        identity.FindFirst(ClaimTypes.Email)!.Value.Should().Be("a@idp.test");
        identity.FindFirst(ClaimTypes.Name)!.Value.Should().Be("Ann");

        var policy = SparkExternalProviderPolicies.For(
            new SparkAuthenticationOptions(), provider.GetServices<SparkExternalProviderRegistration>(), "Idp");
        policy.EmailVerification(new ClaimsPrincipal(identity)).Should().Be(expected);
    }

    [Theory]
    [InlineData("true", SparkEmailVerification.Verified)]
    [InlineData("True", SparkEmailVerification.Verified)]
    [InlineData("false", SparkEmailVerification.Unverified)]
    public void OpenIdConnect_id_token_email_verified_claim_is_honoured(string value, SparkEmailVerification expected)
    {
        var (services, spark) = NewSpark();
        spark.AddOpenIdConnect("Idp", "IdP", o => { o.Authority = "https://idp.test"; o.ClientId = "id"; });
        using var provider = services.BuildServiceProvider();

        // Claims as an id token delivers them with MapInboundClaims = false: JWT names, typed values.
        var identity = new ClaimsIdentity(
            [new Claim("sub", "u1"), new Claim("email_verified", value, ClaimValueTypes.Boolean)], "Idp");
        SparkExternalProviderExtensions.MapOpenIdConnectClaims(identity);

        var policy = SparkExternalProviderPolicies.For(
            new SparkAuthenticationOptions(), provider.GetServices<SparkExternalProviderRegistration>(), "Idp");
        policy.EmailVerification(new ClaimsPrincipal(identity)).Should().Be(expected);
        identity.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("u1");
    }

    [Fact]
    public async Task OpenIdConnect_sign_out_sends_the_client_id()
    {
        var (services, spark) = NewSpark();
        spark.AddOpenIdConnect("Idp", "IdP", o => { o.Authority = "https://idp.test"; o.ClientId = "rp-client"; });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("Idp");

        var context = new RedirectContext(
            new DefaultHttpContext { RequestServices = provider },
            new AuthenticationScheme("Idp", "IdP", typeof(OpenIdConnectHandler)),
            options,
            new AuthenticationProperties())
        {
            ProtocolMessage = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectMessage { IdTokenHint = "token" },
        };

        await options.Events.RedirectToIdentityProviderForSignOut(context);

        context.ProtocolMessage.ClientId.Should().Be("rp-client");
    }
}
