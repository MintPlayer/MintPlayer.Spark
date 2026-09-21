using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// 4d — the account-page endpoints: list what is attached, attach another, detach one.
/// </summary>
/// <remarks>
/// The unlink guard is the reason this exists. Removing an account's last way in is permanent and
/// silent: Identity does it without complaint, and the person finds out at their next sign-in, by
/// which point there is no self-service way back.
/// </remarks>
public class ExternalLoginManagementTests : SparkTestDriver
{
    private const string TestScheme = "TestCookie";

    private UserManager<SparkUser> _userManager = null!;
    private SignInManager<SparkUser> _signInManager = null!;
    private readonly SparkUser _user = new() { Id = "users/alice", Email = "alice@test.org" };

    /// <summary>Signs every request in, so the endpoints' own behaviour is what is under test.</summary>
    private sealed class AlwaysSignedInHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "users/alice")], TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    private async Task<TestServer> StartHostAsync(
        SparkExternalLoginLinking linking,
        SparkLocalCredentials localCredentials = SparkLocalCredentials.Disabled,
        IList<UserLoginInfo>? logins = null,
        bool hasPassword = false)
    {
        _userManager = NewUserManagerStub();
        _signInManager = NewSignInManagerStub(_userManager);

        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(_user);
        _userManager.GetUserIdAsync(_user).Returns(_user.Id!);
        _userManager.GetLoginsAsync(_user).Returns(
            logins ?? [new UserLoginInfo("GitHub", "gh-1", "GitHub")]);
        _userManager.HasPasswordAsync(_user).Returns(hasPassword);
        _userManager.RemoveLoginAsync(_user, Arg.Any<string>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(_user, Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddRouting();
                    services.Configure<SparkAuthenticationOptions>(o => o.ExternalLoginLinking = linking);
                    services.AddAuthentication(TestScheme)
                        .AddScheme<AuthenticationSchemeOptions, AlwaysSignedInHandler>(TestScheme, _ => { })
                        // A named external provider, because LocalCredentials.Disabled refuses to
                        // map an authentication surface nobody could sign into — and Disabled is
                        // the mode these tests care about most.
                        .AddCookie("GitLab", "GitLab", _ => { });

                    // The unlink POST carries antiforgery metadata, so the middleware has to be
                    // present or the endpoint throws. A permissive validator keeps these tests
                    // about the guard rather than about token plumbing, which SparkAntiforgeryTests
                    // already covers.
                    services.AddSingleton(PermissiveAntiforgery());

                    // ConfirmByEmail is refused at startup without a transport — correctly, and
                    // ConfirmByEmailStartupGuardTests is where that belongs. Here it would only
                    // stop the host before the routes exist to inspect.
                    services.AddSingleton<ISparkLinkConfirmationSender<SparkUser>>(
                        Substitute.For<ISparkLinkConfirmationSender<SparkUser>>());
                    services.AddAuthorizationBuilder()
                        .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(TestScheme)
                            .RequireAuthenticatedUser().Build());
                    services.AddScoped(_ => _signInManager);
                    services.AddScoped(_ => _userManager);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseAntiforgery();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapSparkIdentityApi<SparkUser>(localCredentials));
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    private static IReadOnlyList<string> RoutesOf(TestServer server) =>
    [
        .. server.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
    ];

    [Fact]
    public async Task Disabled_maps_no_account_page_endpoints_at_all()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.Disabled);

        RoutesOf(server).Should().NotContain(r => r.Contains("external-logins"),
            "linking is off, so an account page offering it is surface with no purpose");
    }

    [Fact]
    public async Task WhenSignedIn_maps_the_list_the_attach_and_the_detach()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.WhenSignedIn);

        var routes = RoutesOf(server);
        routes.Should().Contain("/spark/auth/external-logins")
            .And.Contain("/spark/auth/external-logins/unlink")
            .And.Contain("/spark/auth/external-logins/link")
            .And.Contain("/spark/auth/link-external-login-callback");
    }

    /// <summary>
    /// ⚠️ ConfirmByEmail gets the unlink but not the attach: its way in is the mailed confirmation,
    /// and without an unlink the links accumulate with no way to undo one.
    /// </summary>
    [Fact]
    public async Task ConfirmByEmail_maps_the_detach_but_not_the_attach()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.ConfirmByEmail);

        var routes = RoutesOf(server);
        routes.Should().Contain("/spark/auth/external-logins")
            .And.Contain("/spark/auth/external-logins/unlink");
        routes.Should().NotContain("/spark/auth/external-logins/link");
    }

    [Fact]
    public async Task The_list_says_whether_each_login_can_be_removed()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.WhenSignedIn);
        using var client = server.CreateClient();

        var body = await client.GetStringAsync("/spark/auth/external-logins");

        body.Should().Contain("\"canUnlink\":false",
            "the client needs it to disable the button rather than offer an action that is refused");
    }

    [Fact]
    public async Task Unlinking_the_only_login_of_a_passwordless_account_is_refused()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.WhenSignedIn);
        using var client = server.CreateClient();

        var response = await client.PostAsync(
            "/spark/auth/external-logins/unlink?provider=GitHub&providerKey=gh-1", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("last_credential");
        await _userManager.DidNotReceive().RemoveLoginAsync(
            Arg.Any<SparkUser>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Unlinking_one_of_two_logins_is_allowed()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.WhenSignedIn, logins:
        [
            new UserLoginInfo("GitHub", "gh-1", "GitHub"),
            new UserLoginInfo("GitLab", "gl-1", "GitLab"),
        ]);
        using var client = server.CreateClient();

        var response = await client.PostAsync(
            "/spark/auth/external-logins/unlink?provider=GitHub&providerKey=gh-1", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _userManager.Received(1).RemoveLoginAsync(_user, "GitHub", "gh-1");
        await _signInManager.Received(1).RefreshSignInAsync(_user);
    }

    /// <summary>
    /// A usable password is a fallback, so the last external login may go — but only where the
    /// application actually serves a password login.
    /// </summary>
    [Theory]
    [InlineData(SparkLocalCredentials.Full, HttpStatusCode.OK)]
    [InlineData(SparkLocalCredentials.Disabled, HttpStatusCode.BadRequest)]
    public async Task A_password_only_rescues_the_last_login_where_it_can_be_used(
        SparkLocalCredentials mode, HttpStatusCode expected)
    {
        using var server = await StartHostAsync(
            SparkExternalLoginLinking.WhenSignedIn, mode, hasPassword: true);
        using var client = server.CreateClient();

        var response = await client.PostAsync(
            "/spark/auth/external-logins/unlink?provider=GitHub&providerKey=gh-1", null);

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Unlinking_a_login_that_is_not_attached_says_so_rather_than_succeeding_quietly()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.WhenSignedIn, logins:
        [
            new UserLoginInfo("GitHub", "gh-1", "GitHub"),
            new UserLoginInfo("GitLab", "gl-1", "GitLab"),
        ]);
        using var client = server.CreateClient();

        var response = await client.PostAsync(
            "/spark/auth/external-logins/unlink?provider=GitHub&providerKey=somebody-else", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("login_not_found");
        await _userManager.DidNotReceive().RemoveLoginAsync(
            Arg.Any<SparkUser>(), Arg.Any<string>(), Arg.Any<string>());
    }

    /// <summary>
    /// ⚠️ "Already attached somewhere" is a fact the user can act on; a store failure is not.
    /// Reporting both the same way sends people looking for the wrong problem.
    /// </summary>
    [Theory]
    [InlineData("LoginAlreadyAssociated", "login_already_associated")]
    [InlineData("ConcurrencyFailure", "link_failed")]
    public async Task A_refused_attach_distinguishes_taken_from_broken(string code, string expected)
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.WhenSignedIn);
        var info = new ExternalLoginInfo(
            new ClaimsPrincipal(new ClaimsIdentity()), "GitLab", "gl-9", "GitLab");
        _signInManager.GetExternalLoginInfoAsync(Arg.Any<string>()).Returns(info);
        _userManager.AddLoginAsync(_user, Arg.Any<UserLoginInfo>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = code }));
        using var client = server.CreateClient();

        var response = await client.GetAsync(
            "/spark/auth/link-external-login-callback?popup=1&returnUrl=%2Faccount");

        (await response.Content.ReadAsStringAsync()).Should().Contain($"error: '{expected}'");
    }

    [Fact]
    public async Task A_successful_attach_refreshes_the_session()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.WhenSignedIn);
        var info = new ExternalLoginInfo(
            new ClaimsPrincipal(new ClaimsIdentity()), "GitLab", "gl-9", "GitLab");
        _signInManager.GetExternalLoginInfoAsync(Arg.Any<string>()).Returns(info);
        using var client = server.CreateClient();

        var response = await client.GetAsync(
            "/spark/auth/link-external-login-callback?popup=1&returnUrl=%2Faccount");

        (await response.Content.ReadAsStringAsync()).Should().Contain("success: true");
        await _userManager.Received(1).AddLoginAsync(_user, Arg.Is<UserLoginInfo>(l =>
            l.LoginProvider == "GitLab" && l.ProviderKey == "gl-9"));
        await _signInManager.Received(1).RefreshSignInAsync(_user);
    }

    /// <summary>
    /// ⚠️ The challenge is keyed on the signed-in user, so the identity coming back is attached to
    /// the session that asked rather than to whoever the callback happens to find signed in.
    /// </summary>
    [Fact]
    public async Task The_attach_challenge_is_keyed_on_the_signed_in_user()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.WhenSignedIn);
        using var client = server.CreateClient();

        await client.GetAsync("/spark/auth/external-logins/link?provider=GitLab&returnUrl=%2Faccount");

        _signInManager.Received(1).ConfigureExternalAuthenticationProperties(
            "GitLab", Arg.Any<string?>(), "users/alice");
    }

    // --- helpers --------------------------------------------------------

    private static IAntiforgery PermissiveAntiforgery()
    {
        var antiforgery = Substitute.For<IAntiforgery>();
        antiforgery.ValidateRequestAsync(Arg.Any<HttpContext>()).Returns(Task.CompletedTask);
        antiforgery.GetAndStoreTokens(Arg.Any<HttpContext>())
            .Returns(new AntiforgeryTokenSet("request", "cookie", "field", "header"));
        return antiforgery;
    }

    private static UserManager<SparkUser> NewUserManagerStub() => Substitute.For<UserManager<SparkUser>>(
        Substitute.For<IUserStore<SparkUser>>(),
        Options.Create(new IdentityOptions()),
        Substitute.For<IPasswordHasher<SparkUser>>(),
        Array.Empty<IUserValidator<SparkUser>>(),
        Array.Empty<IPasswordValidator<SparkUser>>(),
        Substitute.For<ILookupNormalizer>(),
        new IdentityErrorDescriber(),
        Substitute.For<IServiceProvider>(),
        Substitute.For<ILogger<UserManager<SparkUser>>>());

    private static SignInManager<SparkUser> NewSignInManagerStub(UserManager<SparkUser> userManager)
        => Substitute.For<SignInManager<SparkUser>>(
            userManager,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IUserClaimsPrincipalFactory<SparkUser>>(),
            Options.Create(new IdentityOptions()),
            Substitute.For<ILogger<SignInManager<SparkUser>>>(),
            Substitute.For<IAuthenticationSchemeProvider>(),
            Substitute.For<IUserConfirmation<SparkUser>>());
}
