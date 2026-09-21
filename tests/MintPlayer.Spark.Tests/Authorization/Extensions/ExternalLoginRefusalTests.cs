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
/// 4g and 4h — the verified-email gate, and the three places the callback used to carry on when it
/// should have stopped.
/// </summary>
public class ExternalLoginRefusalTests : SparkTestDriver
{
    private SignInManager<SparkUser> _signInManager = null!;
    private UserManager<SparkUser> _userManager = null!;

    private async Task<TestServer> StartHostAsync(Action<SignInManager<SparkUser>, UserManager<SparkUser>> configure)
    {
        _userManager = NewUserManagerStub();
        _signInManager = NewSignInManagerStub(_userManager);
        configure(_signInManager, _userManager);

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAuthentication().AddCookie("GitHub", "GitHub", _ => { });
                    services.AddAuthorization();
                    services.AddRouting();
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

    private static string ErrorFrom(string popupHtml)
    {
        const string marker = "error: '";
        var start = popupHtml.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return "<none>";
        start += marker.Length;
        return popupHtml[start..popupHtml.IndexOf('\'', start)];
    }

    // --- 4g: one claim, failing closed ----------------------------------

    /// <summary>
    /// ⚠️ With D23 there is no confirmation mail, so this gate is the <b>only</b> check that the
    /// address belongs to the person. A provider that does not say must count as not verified.
    /// </summary>
    [Theory]
    [InlineData(null)]              // said nothing
    [InlineData("false")]           // said no
    [InlineData("")]                // said something empty
    [InlineData("1")]               // said something truthy-looking that is not "true"
    [InlineData("urn:github")]      // the old provider-specific vocabulary, now meaningless
    public async Task Anything_short_of_an_explicit_yes_refuses_to_provision(string? claimValue)
    {
        var info = NewLoginInfo("alice@test.org", verifiedClaim: claimValue);
        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(SignInResult.Failed);
        });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?popup=1");

        ErrorFrom(await response.Content.ReadAsStringAsync()).Should().Be("email_not_verified");
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<SparkUser>());
    }

    /// <summary>
    /// The GitHub-specific claim is no longer honoured, which is the point of 4g: one vocabulary
    /// term, so a new forge cannot be silently missed.
    /// </summary>
    [Fact]
    public async Task The_provider_specific_claim_no_longer_counts()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, "alice@test.org"),
            new Claim("urn:github:email_verified", "true"),
        ]));
        var info = new ExternalLoginInfo(principal, "GitHub", "gh-1", "GitHub");

        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(SignInResult.Failed);
        });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?popup=1");

        ErrorFrom(await response.Content.ReadAsStringAsync()).Should().Be("email_not_verified");
    }

    // --- 4h.1: a refused sign-in for a linked account ---------------------

    /// <summary>
    /// ⚠️ The login is attached, so a failed sign-in is a <b>refusal</b>, not a first-time
    /// sign-in. Falling through to provisioning treated a locked-out user as a stranger — and
    /// routing around a lockout is the last thing that should happen to one.
    /// </summary>
    [Theory]
    [InlineData("lockedOut", "locked_out")]
    [InlineData("twoFactor", "requires_two_factor")]
    [InlineData("notAllowed", "not_allowed")]
    [InlineData("plain", "sign_in_refused")]
    public async Task A_refused_sign_in_for_a_linked_account_says_why(string kind, string expected)
    {
        var info = NewLoginInfo("alice@test.org");
        var linked = new SparkUser { Id = "users/alice", Email = "alice@test.org" };
        var refusal = kind switch
        {
            "lockedOut" => SignInResult.LockedOut,
            "twoFactor" => SignInResult.TwoFactorRequired,
            "notAllowed" => SignInResult.NotAllowed,
            _ => SignInResult.Failed,
        };

        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(refusal);
            um.FindByLoginAsync(info.LoginProvider, info.ProviderKey).Returns(linked);
        });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?popup=1");

        ErrorFrom(await response.Content.ReadAsStringAsync()).Should().Be(expected);
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<SparkUser>());
        await _userManager.DidNotReceive().FindByEmailAsync(Arg.Any<string>());
    }

    // --- 4h.2: a half-made account is not left behind ---------------------

    /// <summary>
    /// ⚠️ An account created but not linked is unreachable by anyone, and it holds the email
    /// reservation, so the same person cannot even try again. It exists only because of this
    /// request and has nothing in it, so undoing it beats leaving a tombstone on the address.
    /// </summary>
    [Fact]
    public async Task A_failed_link_does_not_leave_an_unreachable_account_behind()
    {
        var info = NewLoginInfo("new@test.org");
        using var server = await StartHostAsync((sim, um) =>
        {
            sim.GetExternalLoginInfoAsync().Returns(info);
            sim.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(SignInResult.Failed);
            um.SetUserNameAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
            um.SetEmailAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
            um.CreateAsync(Arg.Any<SparkUser>()).Returns(IdentityResult.Success);
            um.AddLoginAsync(Arg.Any<SparkUser>(), Arg.Any<UserLoginInfo>())
                .Returns(IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" }));
        });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?popup=1");

        ErrorFrom(await response.Content.ReadAsStringAsync()).Should().Be("account_creation_failed");
        await _userManager.Received(1).DeleteAsync(Arg.Any<SparkUser>());
        await _signInManager.DidNotReceive().SignInAsync(Arg.Any<SparkUser>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task A_successful_provision_is_not_undone()
    {
        var info = NewLoginInfo("new@test.org");
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

        await client.GetAsync("/spark/auth/external-login-callback?popup=1");

        await _userManager.DidNotReceive().DeleteAsync(Arg.Any<SparkUser>());
        await _signInManager.Received(1).SignInAsync(Arg.Any<SparkUser>(), true, Arg.Any<string?>());
    }

    /// <summary>
    /// 4f: the flag is set because the provider said so, which the gate above already enforced —
    /// so it can never be written for an address nobody attested.
    /// </summary>
    [Fact]
    public async Task A_provisioned_account_is_confirmed_because_the_provider_said_so()
    {
        var info = NewLoginInfo("new@test.org");
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

        await client.GetAsync("/spark/auth/external-login-callback?popup=1");

        await _userManager.Received(1).CreateAsync(Arg.Is<SparkUser>(u => u.EmailConfirmed));
    }

    // --- 4h.3: an unknown provider ----------------------------------------

    /// <summary>
    /// ⚠️ An unregistered scheme reached <c>Results.Challenge</c> and threw, so the answer was a
    /// 500 with a stack trace. It is a bad request — usually a client and a deployment disagreeing
    /// about which providers exist, which a 500 actively hides.
    /// </summary>
    [Fact]
    public async Task An_unknown_provider_is_a_bad_request_rather_than_a_server_error()
    {
        using var server = await StartHostAsync((_, _) => { });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login?provider=NotAProvider");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unknown_provider");
    }

    [Fact]
    public async Task A_registered_provider_still_challenges()
    {
        using var server = await StartHostAsync((_, _) => { });
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login?provider=GitHub");

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

    // --- helpers --------------------------------------------------------

    private static ExternalLoginInfo NewLoginInfo(string email, string? verifiedClaim = "true")
    {
        var claims = new List<Claim> { new(ClaimTypes.Email, email), new(ClaimTypes.Name, "alice") };
        if (verifiedClaim is not null)
            claims.Add(new Claim("email_verified", verifiedClaim));
        return new ExternalLoginInfo(
            new ClaimsPrincipal(new ClaimsIdentity(claims)), "GitHub", "gh-1", "GitHub");
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
