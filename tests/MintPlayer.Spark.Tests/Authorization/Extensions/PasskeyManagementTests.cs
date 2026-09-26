using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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
/// The passkey endpoints a <em>signed-in</em> user reaches: enrollment, listing, rename and delete.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PasskeyEndpointTests"/> covers the anonymous surface — what is mounted and what every
/// refusal is careful not to say. Everything past the first "are you signed in" guard was untested
/// until now, which the coverage tree made plain: the five management classes sat between 15% and
/// 36% while their external-login neighbours were at 92-100%.
/// </para>
/// <para>
/// ⚠️ That gap is not academic. A bare POST to the sign-in route answered 500 with a stack trace for
/// exactly this reason — the body read threw before any guard ran, on a path no test reached. The
/// malformed-body cases below exist so that cannot come back.
/// </para>
/// <para>
/// The delete guard is the other reason this file exists. Removing an account's last credential is
/// permanent and silent: Identity performs it without complaint and the person finds out at their
/// next sign-in, by which point there is no self-service way back.
/// </para>
/// </remarks>
public class PasskeyManagementTests : SparkTestDriver
{
    private const string TestScheme = "TestCookie";

    private static readonly byte[] CredentialId = [1, 2, 3, 4];

    /// <summary>Base64url, matching what the browser produces and what the listing hands out.</summary>
    private static readonly string CredentialIdText =
        Convert.ToBase64String(CredentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private UserManager<SparkUser> _userManager = null!;
    private SignInManager<SparkUser> _signInManager = null!;
    private readonly SparkUser _user = new() { Id = "users/alice", Email = "alice@test.org" };

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

    private static UserPasskeyInfo Passkey(byte[]? id = null, string? name = "laptop") =>
        new(id ?? CredentialId, [7, 7, 7], DateTimeOffset.UtcNow, 1, ["usb"], false, false, true, [3, 3], [4, 4])
        {
            Name = name,
        };

    private async Task<TestServer> StartHostAsync(
        IList<UserPasskeyInfo>? passkeys = null,
        IList<UserLoginInfo>? logins = null,
        bool hasPassword = false,
        SparkLocalCredentials localCredentials = SparkLocalCredentials.Disabled,
        SparkUser? signedInAs = null)
    {
        _userManager = NewUserManagerStub();
        _signInManager = NewSignInManagerStub(_userManager);

        // `signedInAs: null` models an authenticated cookie whose user no longer resolves — the
        // branch every one of these endpoints opens with.
        _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(signedInAs);
        _userManager.GetUserIdAsync(_user).Returns(_user.Id!);
        _userManager.GetUserNameAsync(_user).Returns(_user.Email);
        _userManager.GetLoginsAsync(_user).Returns(logins ?? []);
        _userManager.HasPasswordAsync(_user).Returns(hasPassword);
        _userManager.GetPasskeysAsync(_user).Returns(passkeys ?? [Passkey()]);
        _userManager.GetPasskeyAsync(_user, Arg.Any<byte[]>()).Returns((UserPasskeyInfo?)null);
        _userManager.AddOrUpdatePasskeyAsync(_user, Arg.Any<UserPasskeyInfo>()).Returns(IdentityResult.Success);
        _userManager.RemovePasskeyAsync(_user, Arg.Any<byte[]>()).Returns(IdentityResult.Success);

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    services.AddRouting();
                    services.Configure<SparkAuthenticationOptions>(o =>
                    {
                        o.Passkeys = SparkPasskeys.Enabled;
                        o.LocalCredentials = localCredentials;
                    });
                    services.AddAuthentication(TestScheme)
                        .AddScheme<AuthenticationSchemeOptions, AlwaysSignedInHandler>(TestScheme, _ => { })
                        // A named external provider, because LocalCredentials.Disabled refuses at
                        // startup to map an authentication surface nobody could sign into — and
                        // Disabled is the mode the last-credential guard cares about most.
                        .AddCookie("GitHub", "GitHub", _ => { });
                    // The mutating routes carry antiforgery metadata, so the middleware has to be
                    // present or they throw. A permissive validator keeps these tests about the
                    // endpoints rather than about token plumbing.
                    services.AddSingleton(PermissiveAntiforgery());
                    services.AddAuthorizationBuilder()
                        .SetDefaultPolicy(new AuthorizationPolicyBuilder(TestScheme).RequireAuthenticatedUser().Build());
                    services.AddScoped(_ => _signInManager);
                    services.AddScoped(_ => _userManager);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseAntiforgery();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>(localCredentials));
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    private static HttpContent Raw(string body) =>
        new StringContent(body, Encoding.UTF8, "application/json");

    #region Creation options

    [Fact]
    public async Task Creation_options_are_minted_for_the_signed_in_user()
    {
        using var server = await StartHostAsync(signedInAs: _user);
        _signInManager.MakePasskeyCreationOptionsAsync(Arg.Any<PasskeyUserEntity>())
            .Returns("{\"challenge\":\"abc\"}");

        var response = await server.CreateClient().PostAsync("/spark/auth/passkeys/creation-options", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("challenge");
    }

    [Fact]
    public async Task Creation_options_refuse_a_principal_that_no_longer_resolves()
    {
        using var server = await StartHostAsync(signedInAs: null);

        var response = await server.CreateClient().PostAsync("/spark/auth/passkeys/creation-options", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an authenticated cookie whose user is gone is not an authorization the endpoint may act on");
    }

    #endregion

    #region Register

    [Fact]
    public async Task Registering_with_no_body_at_all_is_a_refusal_rather_than_a_crash()
    {
        using var server = await StartHostAsync(signedInAs: _user);

        var response = await server.CreateClient().PostAsync("/spark/auth/passkeys", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "reading the body by hand throws where minimal-API binding answered 400; an uncaught "
            + "JsonException here is a 500 with a stack trace");
    }

    [Fact]
    public async Task Registering_with_a_malformed_body_is_a_refusal_rather_than_a_crash()
    {
        using var server = await StartHostAsync(signedInAs: _user);

        var response = await server.CreateClient().PostAsync("/spark/auth/passkeys", Raw("not-json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Registering_without_a_credential_is_refused_before_the_ceremony_runs()
    {
        using var server = await StartHostAsync(signedInAs: _user);

        var response = await server.CreateClient()
            .PostAsJsonAsync("/spark/auth/passkeys", new { credentialJson = "", name = "laptop" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _signInManager.DidNotReceive().PerformPasskeyAttestationAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task A_failed_attestation_is_refused()
    {
        using var server = await StartHostAsync(signedInAs: _user);
        _signInManager.PerformPasskeyAttestationAsync(Arg.Any<string>())
            .Returns(PasskeyAttestationResult.Fail(new PasskeyException("no")));

        var response = await server.CreateClient()
            .PostAsJsonAsync("/spark/auth/passkeys", new { credentialJson = "{}", name = "laptop" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _userManager.DidNotReceive().AddOrUpdatePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<UserPasskeyInfo>());
    }

    [Fact]
    public async Task A_ceremony_that_throws_is_refused_rather_than_surfacing_the_exception()
    {
        using var server = await StartHostAsync(signedInAs: _user);
        _signInManager.PerformPasskeyAttestationAsync(Arg.Any<string>())
            .Returns<PasskeyAttestationResult>(_ => throw new PasskeyException("malformed"));

        var response = await server.CreateClient()
            .PostAsJsonAsync("/spark/auth/passkeys", new { credentialJson = "{}", name = "laptop" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_credential_already_held_by_another_account_is_refused_without_saying_so()
    {
        using var server = await StartHostAsync(signedInAs: _user);
        _signInManager.PerformPasskeyAttestationAsync(Arg.Any<string>())
            .Returns(PasskeyAttestationResult.Success(Passkey(), new PasskeyUserEntity
            {
                Id = _user.Id!,
                Name = _user.Email!,
                DisplayName = _user.Email!,
            }));
        // The store refuses a credential id already held by somebody, and refuses without saying by
        // whom. Answering "already registered" here would undo exactly that.
        _userManager.AddOrUpdatePasskeyAsync(_user, Arg.Any<UserPasskeyInfo>())
            .Returns<IdentityResult>(_ => throw new InvalidOperationException("taken"));

        var response = await server.CreateClient()
            .PostAsJsonAsync("/spark/auth/passkeys", new { credentialJson = "{}", name = "laptop" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContainAny(["already", "another", "taken", "users/"],
            "the refusal must not become an oracle for who owns a credential");
    }

    [Fact]
    public async Task A_successful_enrollment_returns_the_credential_summary()
    {
        using var server = await StartHostAsync(signedInAs: _user);
        _signInManager.PerformPasskeyAttestationAsync(Arg.Any<string>())
            .Returns(PasskeyAttestationResult.Success(Passkey(), new PasskeyUserEntity
            {
                Id = _user.Id!,
                Name = _user.Email!,
                DisplayName = _user.Email!,
            }));

        var response = await server.CreateClient()
            .PostAsJsonAsync("/spark/auth/passkeys", new { credentialJson = "{}", name = "laptop" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(CredentialIdText, "the id is handed back base64url, as the browser produced it");
        body.Should().NotContain("publicKey", "metadata only — the key material never leaves the server");
    }

    #endregion

    #region List

    [Fact]
    public async Task Listing_returns_metadata_and_never_the_key_material()
    {
        using var server = await StartHostAsync(signedInAs: _user);

        var response = await server.CreateClient().GetAsync("/spark/auth/passkeys");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(CredentialIdText).And.Contain("laptop");
        body.Should().NotContainAny(["publicKey", "attestation", "clientData"]);
    }

    #endregion

    #region Rename

    [Fact]
    public async Task Renaming_an_unknown_credential_is_a_not_found()
    {
        using var server = await StartHostAsync(signedInAs: _user);

        var response = await server.CreateClient()
            .PostAsJsonAsync($"/spark/auth/passkeys/{CredentialIdText}/name", new { name = "desktop" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Renaming_with_an_undecodable_id_is_refused_before_the_store_is_touched()
    {
        using var server = await StartHostAsync(signedInAs: _user);

        var response = await server.CreateClient()
            .PostAsJsonAsync("/spark/auth/passkeys/a/name", new { name = "desktop" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _userManager.DidNotReceive().GetPasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task Renaming_with_a_malformed_body_is_a_refusal_rather_than_a_crash()
    {
        using var server = await StartHostAsync(signedInAs: _user);
        _userManager.GetPasskeyAsync(_user, Arg.Any<byte[]>()).Returns(Passkey());

        var response = await server.CreateClient()
            .PostAsync($"/spark/auth/passkeys/{CredentialIdText}/name", Raw("not-json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Renaming_stores_the_new_name()
    {
        using var server = await StartHostAsync(signedInAs: _user);
        var existing = Passkey();
        _userManager.GetPasskeyAsync(_user, Arg.Any<byte[]>()).Returns(existing);

        var response = await server.CreateClient()
            .PostAsJsonAsync($"/spark/auth/passkeys/{CredentialIdText}/name", new { name = "desktop" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        existing.Name.Should().Be("desktop");
        await _userManager.Received().AddOrUpdatePasskeyAsync(_user, existing);
    }

    #endregion

    #region Delete — the last-credential guard

    [Fact]
    public async Task Deleting_an_unknown_credential_is_a_not_found()
    {
        using var server = await StartHostAsync(signedInAs: _user);

        var response = await server.CreateClient().DeleteAsync($"/spark/auth/passkeys/{CredentialIdText}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_with_an_undecodable_id_is_refused()
    {
        using var server = await StartHostAsync(signedInAs: _user);

        var response = await server.CreateClient().DeleteAsync("/spark/auth/passkeys/a");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// ⚠️ The guard. One passkey, no external login, no password: removing it locks the account out
    /// permanently, and no amount of support undoes it.
    /// </summary>
    [Fact]
    public async Task Removing_the_only_way_in_is_refused()
    {
        using var server = await StartHostAsync(
            passkeys: [Passkey()], logins: [], hasPassword: false,
            localCredentials: SparkLocalCredentials.Disabled, signedInAs: _user);
        _userManager.GetPasskeyAsync(_user, Arg.Any<byte[]>()).Returns(Passkey());

        var response = await server.CreateClient().DeleteAsync($"/spark/auth/passkeys/{CredentialIdText}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("last_credential",
            "the client keys off this code to explain why, and it is the same shape the unlink route returns");
        await _userManager.DidNotReceive().RemovePasskeyAsync(Arg.Any<SparkUser>(), Arg.Any<byte[]>());
    }

    [Fact]
    public async Task A_password_rescues_the_last_passkey_where_it_can_be_used()
    {
        using var server = await StartHostAsync(
            passkeys: [Passkey()], logins: [], hasPassword: true,
            localCredentials: SparkLocalCredentials.Full, signedInAs: _user);
        _userManager.GetPasskeyAsync(_user, Arg.Any<byte[]>()).Returns(Passkey());

        var response = await server.CreateClient().DeleteAsync($"/spark/auth/passkeys/{CredentialIdText}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _userManager.Received().RemovePasskeyAsync(_user, Arg.Any<byte[]>());
    }

    [Fact]
    public async Task Another_passkey_rescues_the_one_being_removed()
    {
        using var server = await StartHostAsync(
            passkeys: [Passkey(), Passkey([9, 9, 9, 9], "phone")], logins: [], hasPassword: false,
            localCredentials: SparkLocalCredentials.Disabled, signedInAs: _user);
        _userManager.GetPasskeyAsync(_user, Arg.Any<byte[]>()).Returns(Passkey());

        var response = await server.CreateClient().DeleteAsync($"/spark/auth/passkeys/{CredentialIdText}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_external_login_rescues_the_last_passkey()
    {
        using var server = await StartHostAsync(
            passkeys: [Passkey()], logins: [new UserLoginInfo("GitHub", "gh-1", "GitHub")],
            hasPassword: false, localCredentials: SparkLocalCredentials.Disabled, signedInAs: _user);
        _userManager.GetPasskeyAsync(_user, Arg.Any<byte[]>()).Returns(Passkey());

        var response = await server.CreateClient().DeleteAsync($"/spark/auth/passkeys/{CredentialIdText}");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a federated identity is a way in, so the passkey is not the last one");
    }

    #endregion

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
