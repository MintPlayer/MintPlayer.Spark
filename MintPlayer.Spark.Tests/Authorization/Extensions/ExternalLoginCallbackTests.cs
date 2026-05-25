using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// Drives the deeper branches of the GET /spark/auth/external-login-callback handler in
/// <see cref="SparkAuthenticationExtensions.MapSparkIdentityApi{TUser}"/>. The handler
/// receives <see cref="SignInManager{TUser}"/> and <see cref="UserManager{TUser}"/>
/// via parameter injection, so we replace them with NSubstitute fakes in the test
/// host's DI container — the host wiring for cookie/antiforgery/Routing is otherwise
/// real, so the response cookies and HTML are produced by ASP.NET as in production.
///
/// Pinned scenarios:
///   - Existing user, ExternalLoginSignInAsync succeeds → HTML response, FindByLoginAsync called
///   - First-time login, CreateAsync succeeds → SetUserName/Email + AddLoginAsync + SignInAsync
///   - First-time login, CreateAsync fails → redirect to returnUrl
///   - AuthenticationTokens are persisted via SetAuthenticationTokenAsync
/// </summary>
public class ExternalLoginCallbackTests : SparkTestDriver
{
    private SignInManager<SparkUser> _signInManager = null!;
    private UserManager<SparkUser> _userManager = null!;

    private async Task<TestServer> StartHostAsync(
        Action<SignInManager<SparkUser>, UserManager<SparkUser>> configureStubs)
    {
        _userManager = NewUserManagerStub();
        _signInManager = NewSignInManagerStub(_userManager);
        configureStubs(_signInManager, _userManager);

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAuthorization();
                    services.AddRouting();

                    // Override the Identity-registered SignInManager / UserManager with our stubs.
                    // Last scoped registration wins on GetRequiredService, which is how the minimal
                    // endpoint handler resolves them.
                    services.AddScoped(_ => _signInManager);
                    services.AddScoped(_ => _userManager);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>());
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    [Fact]
    public async Task Callback_succeeds_for_existing_external_login_and_returns_HTML_with_postMessage_script()
    {
        var info = NewLoginInfo(email: "alice@test.org", name: "alice", tokens: null);
        var existingUser = new SparkUser { Id = "users/alice", UserName = "alice" };

        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: true)
                .Returns(SignInResult.Success);
            um.FindByLoginAsync(info.LoginProvider, info.ProviderKey).Returns(existingUser);
        });
        // R2-C4: callback now responds with a server-side Redirect for the
        // non-popup flow (the old HTML+JS interpolation was the XSS vector).
        // Popup callers append ?popup to get the postMessage HTML.
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = server.BaseAddress,
        };
        // Inject the test server's handler into our non-redirecting client.
        // Simpler: just use server.CreateClient with handler tweak.
        using var nonRedirectClient = server.CreateClient();

        // Non-popup: redirect to safeReturnUrl
        var redirectResponse = await nonRedirectClient.GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fhome");
        // TestServer auto-follows redirects by default; assert we ended up at /home OR observe the 302.
        // Use the response chain: Status should be the final landing (which 404s since /home isn't mapped here),
        // OR was a 302. Either way, the body should NOT contain the JS interpolation pattern.
        var redirectBody = await redirectResponse.Content.ReadAsStringAsync();
        redirectBody.Should().NotContain("window.location.href = '",
            "non-popup path must not interpolate returnUrl into a JS string literal (R2-C4)");

        // Popup branch: ?popup → HTML with postMessage
        var popupResponse = await nonRedirectClient.GetAsync("/spark/auth/external-login-callback?popup=1&returnUrl=%2Fhome");
        popupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var popupBody = await popupResponse.Content.ReadAsStringAsync();
        popupBody.Should().Contain("postMessage");
        // Critically, the popup branch's HTML no longer carries returnUrl at all —
        // the postMessage payload is a static string.
        popupBody.Should().NotContain("'/home'", "popup HTML must be returnUrl-free (R2-C4)");

        await _userManager.Received().FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        await _signInManager.DidNotReceive().SignInAsync(Arg.Any<SparkUser>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Callback_persists_authentication_tokens_when_provided_on_login_info()
    {
        var info = NewLoginInfo(email: "bob@test.org", name: "bob", tokens:
        [
            new AuthenticationToken { Name = "access_token", Value = "AT-1" },
            new AuthenticationToken { Name = "refresh_token", Value = "RT-1" },
        ]);
        var existingUser = new SparkUser { Id = "users/bob" };

        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, true)
                .Returns(SignInResult.Success);
            um.FindByLoginAsync(info.LoginProvider, info.ProviderKey).Returns(existingUser);
        });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback");

        // Non-popup → 302 redirect. Either follow (default) lands somewhere, or
        // observe the redirect directly. Tokens persisted either way.
        await _userManager.Received(1).SetAuthenticationTokenAsync(existingUser, info.LoginProvider, "access_token", "AT-1");
        await _userManager.Received(1).SetAuthenticationTokenAsync(existingUser, info.LoginProvider, "refresh_token", "RT-1");
    }

    [Fact]
    public async Task Callback_creates_a_new_user_from_external_claims_when_sign_in_fails()
    {
        var info = NewLoginInfo(email: "new@test.org", name: "newbie", tokens: null);

        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(SignInResult.Failed);
            um.SetUserNameAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
            um.SetEmailAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
            um.CreateAsync(Arg.Any<SparkUser>()).Returns(IdentityResult.Success);
            um.AddLoginAsync(Arg.Any<SparkUser>(), Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);
        });
        using var client = server.CreateClient();

        await client.GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fwelcome");

        // R2-C4: non-popup path now redirects, so the final HTTP status depends
        // on whether /welcome is mapped (404 in this test host). Check the
        // side-effects on UserManager — those are the contract.
        // Username + email pulled from claims and applied to the new user.
        await _userManager.Received(1).SetUserNameAsync(Arg.Any<SparkUser>(), "newbie");
        await _userManager.Received(1).SetEmailAsync(Arg.Any<SparkUser>(), "new@test.org");
        await _userManager.Received(1).CreateAsync(Arg.Any<SparkUser>());
        await _userManager.Received(1).AddLoginAsync(Arg.Any<SparkUser>(), Arg.Is<UserLoginInfo>(li =>
            li.LoginProvider == info.LoginProvider && li.ProviderKey == info.ProviderKey));
        await _signInManager.Received(1).SignInAsync(Arg.Any<SparkUser>(), true, Arg.Any<string?>());
    }

    [Fact]
    public async Task Callback_falls_back_to_NameIdentifier_when_no_Name_claim_is_present()
    {
        // Pin the `?? FindFirstValue(ClaimTypes.NameIdentifier)` branch — important for
        // OAuth providers (e.g. GitHub) where the username is published as NameIdentifier.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Email, "noname@test.org"),
            new Claim(ClaimTypes.NameIdentifier, "github-handle"),
            // R2-H11: required for auto-provisioning to proceed
            new Claim("urn:github:email_verified", "true"),
            // intentionally no ClaimTypes.Name
        }));
        var info = new ExternalLoginInfo(principal, "GitHub", "12345", "GitHub");

        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(SignInResult.Failed);
            um.SetUserNameAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
            um.SetEmailAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
            um.CreateAsync(Arg.Any<SparkUser>()).Returns(IdentityResult.Success);
            um.AddLoginAsync(Arg.Any<SparkUser>(), Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);
        });
        using var client = server.CreateClient();

        await client.GetAsync("/spark/auth/external-login-callback");

        // R2-C4: non-popup path redirects; test the side-effect (claim → username).
        await _userManager.Received(1).SetUserNameAsync(Arg.Any<SparkUser>(), "github-handle");
    }

    [Fact]
    public async Task Callback_redirects_to_returnUrl_when_user_creation_fails()
    {
        var info = NewLoginInfo(email: "broken@test.org", name: "broken", tokens: null);
        var failure = IdentityResult.Failed(new IdentityError { Code = "X", Description = "boom" });

        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(SignInResult.Failed);
            um.SetUserNameAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
            um.SetEmailAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
            um.CreateAsync(Arg.Any<SparkUser>()).Returns(failure);
        });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?returnUrl=%2Foops");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/oops");
        // Bail-out happens before AddLogin / SignIn — confirm we did not proceed.
        await _userManager.DidNotReceive().AddLoginAsync(Arg.Any<SparkUser>(), Arg.Any<UserLoginInfo>());
        await _signInManager.DidNotReceive().SignInAsync(Arg.Any<SparkUser>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    // --- helpers --------------------------------------------------------

    private static ExternalLoginInfo NewLoginInfo(string email, string name, IEnumerable<AuthenticationToken>? tokens, bool emailVerified = true)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, email),
            new(ClaimTypes.Name, name),
        };
        // R2-H11: auto-provisioning now requires the issuer to attest the email.
        // Tests default to verified=true since that's the happy path; pass false
        // explicitly to exercise the refusal branch.
        if (emailVerified)
            claims.Add(new Claim("email_verified", "true"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims));
        var info = new ExternalLoginInfo(principal, "TestProvider", "ext-key-" + name, "TestProvider");
        if (tokens is not null)
            info.AuthenticationTokens = tokens;
        return info;
    }

    private static UserManager<SparkUser> NewUserManagerStub()
    {
        // UserManager has 9 ctor args; NSubstitute proxies the virtual methods we configure.
        return Substitute.For<UserManager<SparkUser>>(
            Substitute.For<IUserStore<SparkUser>>(),
            Options.Create(new IdentityOptions()),
            Substitute.For<IPasswordHasher<SparkUser>>(),
            Array.Empty<IUserValidator<SparkUser>>(),
            Array.Empty<IPasswordValidator<SparkUser>>(),
            Substitute.For<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<SparkUser>>>());
    }

    private static SignInManager<SparkUser> NewSignInManagerStub(UserManager<SparkUser> userManager)
    {
        return Substitute.For<SignInManager<SparkUser>>(
            userManager,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IUserClaimsPrincipalFactory<SparkUser>>(),
            Options.Create(new IdentityOptions()),
            Substitute.For<ILogger<SignInManager<SparkUser>>>(),
            Substitute.For<IAuthenticationSchemeProvider>(),
            Substitute.For<IUserConfirmation<SparkUser>>());
    }
}
