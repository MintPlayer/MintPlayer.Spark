using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.IdentityProvider;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// #460 D4 — sign in with email or user name — and spike SP-A: the second-factor and recovery-code
/// steps under the <see cref="SparkSignInManager{TUser}"/> override, in cookie and bearer mode, through
/// <c>MapIdentityApi</c>'s real <c>/login</c>.
/// </summary>
public class SignInIdentifierTests : SparkTestDriver
{
    private static object Login(string identifier, string password = AccountTestHost.Password, string? code = null, string? recovery = null)
        => new { email = identifier, password, twoFactorCode = code, twoFactorRecoveryCode = recovery };

    private static string Url(bool useCookies) => useCookies ? "/spark/auth/login?useCookies=true" : "/spark/auth/login";

    [Fact]
    public async Task Password_sign_in_accepts_the_email_and_the_user_name()
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        await host.CreateUserAsync("jdoe", "jane@example.com");
        await WaitForIndexesAsync();
        using var client = host.Client();

        var byEmail = await client.PostAsJsonAsync("/spark/auth/login", Login("jane@example.com"));
        var byName = await client.PostAsJsonAsync("/spark/auth/login", Login("jdoe"));
        var unknown = await client.PostAsJsonAsync("/spark/auth/login", Login("nobody"));

        byEmail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await byEmail.Content.ReadAsStringAsync()).Should().Contain("accessToken");
        byName.StatusCode.Should().Be(HttpStatusCode.OK);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private const SparkSignInIdentifiers Either = SparkSignInIdentifiers.Email | SparkSignInIdentifiers.UserName;

    public static TheoryData<SparkSignInIdentifiers, bool, bool> IdentifierCases()
    {
        var data = new TheoryData<SparkSignInIdentifiers, bool, bool>();
        foreach (var allowed in new[] { SparkSignInIdentifiers.Email, SparkSignInIdentifiers.UserName, Either })
            foreach (var byEmail in new[] { true, false })
                foreach (var useCookies in new[] { true, false })
                    data.Add(allowed, byEmail, useCookies);
        return data;
    }

    [Theory]
    [MemberData(nameof(IdentifierCases))]
    public async Task Sign_in_accepts_only_the_identifier_kinds_the_application_allows(
        SparkSignInIdentifiers allowed, bool byEmail, bool useCookies)
    {
        await using var host = await AccountTestHost.StartAsync(Store, configure: o => o.SignInIdentifiers = allowed);
        var user = await host.CreateUserAsync("kinds", "kinds@example.com");
        await WaitForIndexesAsync();
        using var client = host.Client();

        var response = await client.PostAsJsonAsync(Url(useCookies), Login(byEmail ? "kinds@example.com" : "kinds"));

        var kindAllowed = allowed.HasFlag(byEmail ? SparkSignInIdentifiers.Email : SparkSignInIdentifiers.UserName);
        if (kindAllowed)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return;
        }

        // A disallowed kind is an unknown account: same status, same body, as an identifier of an
        // allowed kind that names nobody.
        var unknown = await client.PostAsJsonAsync(Url(useCookies),
            Login(allowed.HasFlag(SparkSignInIdentifiers.Email) ? "nobody@example.com" : "nobody"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Be(await unknown.Content.ReadAsStringAsync());

        // ...and it is not a failed attempt against the account it would have named.
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<SparkUser>(user.Id)).AccessFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task An_application_that_allows_no_identifier_is_refused_at_startup()
    {
        var act = async () => await AccountTestHost.StartAsync(Store, configure: o => o.SignInIdentifiers = 0);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("SignInIdentifiers");
    }

    [Fact]
    public async Task Without_password_sign_in_the_identifiers_are_not_read()
    {
        // LocalCredentials Disabled maps no /login, so an empty setting refuses nothing.
        await using var host = await AccountTestHost.StartAsync(
            Store, SparkLocalCredentials.Disabled, configure: o => o.SignInIdentifiers = 0);
    }

    [Theory]
    [InlineData(SparkSignInIdentifiers.Email, "Email", "type=\"email\"")]
    [InlineData(SparkSignInIdentifiers.UserName, "User name", "type=\"text\"")]
    public async Task The_oidc_login_page_honours_the_identifier_kinds(SparkSignInIdentifiers allowed, string label, string inputType)
    {
        await using var factory = new SparkEndpointFactory<OidcTestContext>(
            Store,
            models: [],
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(configure: auth =>
                {
                    auth.LocalCredentials = SparkLocalCredentials.Full;
                    auth.AllowUnconfirmedRegistration = true;
                    auth.SignInIdentifiers = allowed;
                });
                spark.AddIdentityProvider(options =>
                {
                    options.Issuer = "https://idp.test";
                    options.SigningKeyPath = Path.Combine(
                        Path.GetTempPath(), "spark-oidc-test-" + Guid.NewGuid().ToString("N") + ".json");
                });
            },
            environment: "Development");

        using (var scope = factory.GetService<IServiceScopeFactory>().CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>()
                .CreateAsync(new SparkUser { UserName = "oidc-kinds", Email = "oidc-kinds@example.com", EmailConfirmed = true }, AccountTestHost.Password);
            created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        }
        await WaitForIndexesAsync();

        async Task<(string Page, HttpResponseMessage Response)> AttemptAsync(string identifier)
        {
            var browser = new OidcTestHost.Browser(factory.CreateClient());
            var page = await (await browser.GetAsync("/connect/login?returnUrl=%2F")).Content.ReadAsStringAsync();
            var response = await browser.PostFormAsync("/connect/login", new Dictionary<string, string>
            {
                ["identifier"] = identifier, ["password"] = AccountTestHost.Password, ["returnUrl"] = "/",
                ["__RequestVerificationToken"] = OidcTestHost.AntiforgeryTokenFrom(page),
            });
            return (page, response);
        }

        var emailAllowed = allowed == SparkSignInIdentifiers.Email;
        var (page, accepted) = await AttemptAsync(emailAllowed ? "oidc-kinds@example.com" : "oidc-kinds");
        var (_, refused) = await AttemptAsync(emailAllowed ? "oidc-kinds" : "oidc-kinds@example.com");

        page.Should().Contain($"<label for=\"identifier\">{label}</label>").And.Contain(inputType);
        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        accepted.Headers.Location!.OriginalString.Should().Be("/");
        refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
        refused.Headers.Location!.OriginalString.Should().Contain("error=invalid_credentials");
    }

    [Fact]
    public async Task An_unmatched_email_no_longer_falls_back_to_a_user_name()
    {
        // A leftover '@' user name (written past the validator, as before the migration) is not a
        // way in: an identifier with '@' is looked up as an email only.
        await using var host = await AccountTestHost.StartAsync(Store);
        // Created through UserManager (the email lookup is a compare-exchange entry it writes), then
        // the user name is rewritten in the document, past the validator.
        var created = await host.CreateUserAsync("current", "current@example.com");
        using (var session = Store.OpenAsyncSession())
        {
            var legacy = await session.LoadAsync<SparkUser>(created.Id);
            legacy.UserName = "old@example.com";
            legacy.NormalizedUserName = "OLD@EXAMPLE.COM";
            await session.SaveChangesAsync();
        }
        await WaitForIndexesAsync();
        using var client = host.Client();

        var byOldName = await client.PostAsJsonAsync("/spark/auth/login", Login("old@example.com"));
        var byEmail = await client.PostAsJsonAsync("/spark/auth/login", Login("current@example.com"));

        byOldName.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        byEmail.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_password_never_falls_through_to_a_second_account()
    {
        // Account B predates the '@' rule: its user name is account A's email. The identifier names A
        // (found by email), so B's password must not sign anyone in, and only A pays the failed attempt.
        await using var host = await AccountTestHost.StartAsync(Store);
        var a = await host.CreateUserAsync("alpha", "a@example.com", password: "Alpha-password-1");

        var hasher = new PasswordHasher<SparkUser>();
        var legacy = new SparkUser
        {
            UserName = "a@example.com",
            NormalizedUserName = "A@EXAMPLE.COM",
            Email = "b@example.com",
            NormalizedEmail = "B@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
        };
        legacy.PasswordHash = hasher.HashPassword(legacy, "Bravo-password-1");
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(legacy);
            await session.SaveChangesAsync();
        }
        await WaitForIndexesAsync();
        using var client = host.Client();

        var withBsPassword = await client.PostAsJsonAsync("/spark/auth/login", Login("a@example.com", "Bravo-password-1"));
        var withAsPassword = await client.PostAsJsonAsync("/spark/auth/login", Login("a@example.com", "Alpha-password-1"));

        withBsPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withAsPassword.StatusCode.Should().Be(HttpStatusCode.OK);

        using var check = Store.OpenAsyncSession();
        (await check.LoadAsync<SparkUser>(legacy.Id)).AccessFailedCount.Should().Be(0);
        // A's failure was recorded and then reset by its successful sign-in.
        (await check.LoadAsync<SparkUser>(a.Id)).AccessFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task A_user_name_never_contains_an_at_sign_not_even_the_accounts_own_email()
    {
        // G-Q22: the user name is public (labels, history), so it may never be an email address.
        await using var host = await AccountTestHost.StartAsync(Store);

        var foreign = await host.WithScopeAsync(sp => sp.GetRequiredService<UserManager<SparkUser>>()
            .CreateAsync(new SparkUser { UserName = "x@example.com", Email = "y@example.com" }, AccountTestHost.Password));
        var own = await host.WithScopeAsync(sp => sp.GetRequiredService<UserManager<SparkUser>>()
            .CreateAsync(new SparkUser { UserName = "y@example.com", Email = "y@example.com" }, AccountTestHost.Password));
        var handle = await host.WithScopeAsync(sp => sp.GetRequiredService<UserManager<SparkUser>>()
            .CreateAsync(new SparkUser { UserName = "y-handle", Email = "y@example.com" }, AccountTestHost.Password));

        foreign.Errors.Select(e => e.Code).Should().Contain(SparkUserNameValidator<SparkUser>.ErrorCode);
        own.Errors.Select(e => e.Code).Should().Contain(SparkUserNameValidator<SparkUser>.ErrorCode);
        handle.Succeeded.Should().BeTrue(string.Join("; ", handle.Errors.Select(e => e.Code)));
    }

    [Fact]
    public async Task A_rename_to_an_email_address_is_refused()
    {
        // Every path goes through the validator, not only registration: here an update.
        await using var host = await AccountTestHost.StartAsync(Store);
        var created = await host.CreateUserAsync("renamer", "renamer@example.com");

        var result = await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            // By id: FindByName/FindByEmail query an index, and right after the create that index can
            // still be stale under load (null user, seen in the #264 sweep). A load by id cannot be.
            var user = (await users.FindByIdAsync(created.Id!))!;
            return await users.SetUserNameAsync(user, "renamer@example.com");
        });

        result.Errors.Select(e => e.Code).Should().Contain(SparkUserNameValidator<SparkUser>.ErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SP_A_second_factor_by_user_name(bool useCookies)
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        var (key, _) = await EnableTwoFactorAsync(host, "tfa-user", "tfa@example.com");
        await WaitForIndexesAsync();
        using var client = host.Client();

        var passwordOnly = await client.PostAsJsonAsync(Url(useCookies), Login("tfa-user"));
        var wrongCode = await client.PostAsJsonAsync(Url(useCookies), Login("tfa-user", code: "000000"));
        var withCode = await client.PostAsJsonAsync(Url(useCookies), Login("tfa-user", code: AccountTestHost.Totp(key)));

        passwordOnly.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await passwordOnly.Content.ReadAsStringAsync()).Should().Contain("RequiresTwoFactor");
        wrongCode.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        withCode.StatusCode.Should().Be(HttpStatusCode.OK);

        if (useCookies)
            AccountTestHost.CookieHeader(withCode).Should().Contain(".AspNetCore.Identity.Application=");
        else
            (await withCode.Content.ReadAsStringAsync()).Should().Contain("accessToken");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SP_A_recovery_code_by_email_is_single_use(bool useCookies)
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        var (_, id) = await EnableTwoFactorAsync(host, "rc-user", "rc@example.com");
        var codes = await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            // By id: FindByEmail right after the create can hit a stale index under load.
            var user = await users.FindByIdAsync(id);
            return (await users.GenerateNewTwoFactorRecoveryCodesAsync(user!, 2))!.ToArray();
        });
        await WaitForIndexesAsync(); // the sign-in itself resolves the email through the index
        using var client = host.Client();

        var first = await client.PostAsJsonAsync(Url(useCookies), Login("rc@example.com", recovery: codes[0]));
        var replay = await client.PostAsJsonAsync(Url(useCookies), Login("rc@example.com", recovery: codes[0]));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_confirmed_email_change_keeps_the_user_name()
    {
        // G-Q22: the email change used to rewrite an email-shaped user name; it never touches it now.
        await using var host = await AccountTestHost.StartAsync(Store);
        var created = await host.CreateUserAsync("handle", "handle-old@example.com");

        async Task<SparkUser> ChangeAsync(string to) => await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            // By id, not FindByEmail: an index query right after the create can be stale under load.
            var user = (await users.FindByIdAsync(created.Id!))!;
            var token = await users.GenerateChangeEmailTokenAsync(user, to);
            var result = await users.ChangeEmailAsync(user, to, token);
            result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Code)));
            return user;
        });

        var changed = await ChangeAsync("handle-new@example.com");

        changed.UserName.Should().Be("handle");
        changed.Email.Should().Be("handle-new@example.com");
    }

    [Fact]
    public async Task Every_sign_in_stamps_when_it_happened()
    {
        // The re-authentication window for account deletion reads this; a cookie refresh keeps it.
        await using var host = await AccountTestHost.StartAsync(Store);
        await host.CreateUserAsync("stamp", "stamp@example.com");
        using var client = host.Client();

        var cookie = await host.CookieSignInAsync(client, "stamp@example.com");
        var ticket = await host.WithScopeAsync(async sp =>
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = sp };
            http.Request.Headers.Cookie = cookie;
            sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext = http;
            var signIn = (SparkSignInManager<SparkUser>)sp.GetRequiredService<SignInManager<SparkUser>>();
            return await signIn.GetAuthenticatedAtAsync();
        });

        ticket.HasValue.Should().BeTrue();
        (DateTimeOffset.UtcNow - ticket!.Value).Should().BeLessThan(TimeSpan.FromMinutes(1));
    }

    private static Task<(string Key, string Id)> EnableTwoFactorAsync(AccountTestHost host, string userName, string email)
        => host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var user = new SparkUser { UserName = userName, Email = email, EmailConfirmed = true };
            (await users.CreateAsync(user, AccountTestHost.Password)).Succeeded.Should().BeTrue();
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
            return ((await users.GetAuthenticatorKeyAsync(user))!, user.Id!);
        });
}
