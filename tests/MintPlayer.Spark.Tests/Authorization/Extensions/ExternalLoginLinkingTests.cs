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
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// What the external-login callback does when the provider's address already belongs to an account.
/// </summary>
/// <remarks>
/// <para>
/// Before this, every one of these ended as <c>account_creation_failed</c> — the store's
/// <c>DuplicateEmail</c> surfacing through a branch that could not tell a duplicate from a
/// validation error. The three modes differ in <b>who proves what</b>, so each gets its own answer.
/// </para>
/// <para>
/// The popup branch is the one asserted, because that is the one the client actually uses; the
/// redirect branch gets its own test below since it used to drop the outcome entirely.
/// </para>
/// </remarks>
public class ExternalLoginLinkingTests : SparkTestDriver
{
    private SignInManager<SparkUser> _signInManager = null!;
    private UserManager<SparkUser> _userManager = null!;
    private RecordingSender _sender = null!;

    /// <summary>
    /// ⚠️ A fresh id per test. The pending-link document's key is derived from the account, and the
    /// RavenDB database is shared across the class — with a fixed id, one test's pending link
    /// suppresses the next test's mail, which is the suppression rule working correctly against the
    /// wrong thing.
    /// </summary>
    private SparkUser NewExistingUser() => new()
    {
        Id = "users/alice-" + Guid.NewGuid().ToString("N"),
        Email = "alice@test.org",
    };

    private sealed class RecordingSender : ISparkLinkConfirmationSender<SparkUser>
    {
        public List<string> Links { get; } = [];

        public Task SendLinkConfirmationAsync(
            SparkUser user, string providerDisplayName, string? providerIdentity,
            string confirmationLink, CancellationToken cancellationToken = default)
        {
            Links.Add(confirmationLink);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A host whose provider sign-in fails and whose email is already taken — the 4c situation.
    /// </summary>
    private async Task<TestServer> StartHostAsync(SparkExternalLoginLinking linking, SparkUser? existing)
    {
        var info = NewLoginInfo("alice@test.org", "alice");
        _userManager = NewUserManagerStub();
        _signInManager = NewSignInManagerStub(_userManager);
        _sender = new RecordingSender();

        _signInManager.GetExternalLoginInfoAsync().Returns(info);
        _signInManager.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(SignInResult.Failed);
        _userManager.FindByEmailAsync("alice@test.org").Returns(existing);
        _userManager.SetUserNameAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
        _userManager.SetEmailAsync(Arg.Any<SparkUser>(), Arg.Any<string?>()).Returns(IdentityResult.Success);
        _userManager.CreateAsync(Arg.Any<SparkUser>()).Returns(IdentityResult.Success);
        _userManager.AddLoginAsync(Arg.Any<SparkUser>(), Arg.Any<UserLoginInfo>()).Returns(IdentityResult.Success);

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddAuthorization();
                    services.AddRouting();
                    services.Configure<SparkAuthenticationOptions>(o => o.ExternalLoginLinking = linking);
                    services.AddSingleton<ISparkLinkConfirmationSender<SparkUser>>(_sender);
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

    [Theory]
    [InlineData(SparkExternalLoginLinking.Disabled, "email_already_registered")]
    [InlineData(SparkExternalLoginLinking.WhenSignedIn, "sign_in_to_link")]
    [InlineData(SparkExternalLoginLinking.ConfirmByEmail, "link_confirmation_sent")]
    public async Task A_known_address_gets_an_answer_that_names_the_mode(
        SparkExternalLoginLinking linking, string expected)
    {
        using var server = await StartHostAsync(linking, NewExistingUser());
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?popup=1&returnUrl=%2Fhome");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ErrorFrom(await response.Content.ReadAsStringAsync()).Should().Be(expected,
            "'account_creation_failed' read as 'this application is broken' rather than 'you "
            + "already have an account', which is the one answer that is always wrong");

        await _userManager.DidNotReceive().CreateAsync(Arg.Any<SparkUser>());
    }

    [Fact]
    public async Task An_unknown_address_still_provisions()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.Disabled, existing: null);
        using var client = server.CreateClient();

        await client.GetAsync("/spark/auth/external-login-callback?popup=1");

        await _userManager.Received(1).CreateAsync(Arg.Any<SparkUser>());
    }

    [Fact]
    public async Task ConfirmByEmail_mails_a_link_instead_of_signing_anybody_in()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.ConfirmByEmail, NewExistingUser());
        using var client = server.CreateClient();

        await client.GetAsync("/spark/auth/external-login-callback?popup=1&returnUrl=%2Fhome");

        _sender.Links.Should().ContainSingle();
        _sender.Links[0].Should().Contain("/spark/auth/confirm-external-link?token=")
            .And.Contain("returnUrl=%2Fhome",
                "the confirmation has to land where the sign-in was heading, or the link ends nowhere");
        await _signInManager.DidNotReceive().SignInAsync(Arg.Any<SparkUser>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    /// <summary>
    /// ⚠️ The redirect branch used to drop the outcome, so a full-page sign-in landed back where it
    /// started with nothing to show. Survivable while every refusal meant "it did not work" — not
    /// survivable now that one of them means "check your mail".
    /// </summary>
    [Fact]
    public async Task The_redirect_branch_carries_the_outcome()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.Disabled, NewExistingUser());
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fsign-in");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString
            .Should().Be("/sign-in?sparkExternalLogin=email_already_registered");
    }

    [Fact]
    public async Task A_successful_sign_in_redirects_without_a_code()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.Disabled, existing: null);
        _signInManager.ExternalLoginSignInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(SignInResult.Success);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/external-login-callback?returnUrl=%2Fhome");

        response.Headers.Location!.OriginalString.Should().Be("/home",
            "success has nothing to report, and a query string on every landing would be noise");
    }

    /// <summary>
    /// Following the mailed link attaches the login and signs the person in — the point of proving
    /// control of the mailbox is not having to prove anything a second time.
    /// </summary>
    [Fact]
    public async Task The_confirmation_link_links_and_signs_in()
    {
        var existing = NewExistingUser();
        using var server = await StartHostAsync(SparkExternalLoginLinking.ConfirmByEmail, existing);
        _userManager.FindByIdAsync(existing.Id!).Returns(existing);
        using var client = server.CreateClient();

        await client.GetAsync("/spark/auth/external-login-callback?popup=1&returnUrl=%2Fhome");
        var confirmUrl = _sender.Links[0][_sender.Links[0].IndexOf("/spark/auth/", StringComparison.Ordinal)..];

        // The link is followed from a mailbox, so nothing external is presented on this request.
        _signInManager.GetExternalLoginInfoAsync().Returns((ExternalLoginInfo?)null);
        var response = await client.GetAsync(confirmUrl);

        response.Headers.Location!.OriginalString.Should().Be("/home?sparkLinkConfirmation=linked");
        await _userManager.Received(1).AddLoginAsync(existing, Arg.Is<UserLoginInfo>(li =>
            li.ProviderKey == "ext-key-alice"));
        await _signInManager.Received(1).SignInAsync(existing, true, Arg.Any<string?>());
    }

    [Fact]
    public async Task A_confirmation_with_no_token_says_so_without_signing_anyone_in()
    {
        using var server = await StartHostAsync(SparkExternalLoginLinking.ConfirmByEmail, existing: null);
        _signInManager.GetExternalLoginInfoAsync().Returns((ExternalLoginInfo?)null);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/spark/auth/confirm-external-link?returnUrl=%2Fhome");

        response.Headers.Location!.OriginalString
            .Should().Be("/home?sparkLinkConfirmation=invalid_or_expired");
        await _signInManager.DidNotReceive().SignInAsync(Arg.Any<SparkUser>(), Arg.Any<bool>(), Arg.Any<string?>());
    }

    // --- helpers --------------------------------------------------------

    private static ExternalLoginInfo NewLoginInfo(string email, string name)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, name),
            new Claim("email_verified", "true"),
        ]));
        return new ExternalLoginInfo(principal, "TestProvider", "ext-key-" + name, "TestProvider");
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
