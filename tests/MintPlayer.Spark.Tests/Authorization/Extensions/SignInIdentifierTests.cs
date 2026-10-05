using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;

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
        await host.CreateUserAsync("renamer", "renamer@example.com");

        var result = await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var user = (await users.FindByNameAsync("renamer"))!;
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
        var key = await EnableTwoFactorAsync(host, "tfa-user", "tfa@example.com");
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
        await EnableTwoFactorAsync(host, "rc-user", "rc@example.com");
        var codes = await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var user = await users.FindByEmailAsync("rc@example.com");
            return (await users.GenerateNewTwoFactorRecoveryCodesAsync(user!, 2))!.ToArray();
        });
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
        await host.CreateUserAsync("handle", "handle-old@example.com");

        async Task<SparkUser> ChangeAsync(string from, string to) => await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var user = (await users.FindByEmailAsync(from))!;
            var token = await users.GenerateChangeEmailTokenAsync(user, to);
            var result = await users.ChangeEmailAsync(user, to, token);
            result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Code)));
            return user;
        });

        var changed = await ChangeAsync("handle-old@example.com", "handle-new@example.com");

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

    private static Task<string> EnableTwoFactorAsync(AccountTestHost host, string userName, string email)
        => host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var user = new SparkUser { UserName = userName, Email = email, EmailConfirmed = true };
            (await users.CreateAsync(user, AccountTestHost.Password)).Succeeded.Should().BeTrue();
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
            return (await users.GetAuthenticatorKeyAsync(user))!;
        });
}
