using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Builder;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// #460 D6 (confirmed email, reset confirms), D8 (personal data, deletion) and D16 (account pages,
/// SPA links) through the real endpoints.
/// </summary>
public class AccountFlowTests : SparkTestDriver
{
    #region D6 — confirmation and recovery

    [Fact]
    public async Task Registration_mails_a_link_to_the_SPA_and_a_reset_of_an_unconfirmed_account_confirms_it()
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        using var client = host.Client();

        (await client.PostAsJsonAsync("/spark/auth/register", new { email = "new@example.com", password = AccountTestHost.Password, userName = "newcomer" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var confirmation = host.Mail.Sent.Should().ContainSingle().Which;
        confirmation.Kind.Should().Be("confirm");
        confirmation.Link.Should().StartWith($"{AccountTestHost.BaseUrl}/confirm-email?userId=");
        confirmation.Query("code").Should().NotBeNullOrEmpty();

        // D6: Microsoft's forgotPassword ignores unconfirmed addresses; Spark's sends.
        (await client.PostAsJsonAsync("/spark/auth/forgotPassword", new { email = "new@example.com" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var reset = host.Mail.Sent.Last();
        reset.Kind.Should().Be("reset-link");
        reset.Link.Should().StartWith($"{AccountTestHost.BaseUrl}/reset-password?email=");
        reset.Query("email").Should().Be("new@example.com");

        var resetResponse = await client.PostAsJsonAsync("/spark/auth/resetPassword", new
        {
            email = "new@example.com",
            resetCode = reset.Query("code"),
            newPassword = "Brand-new-password-2",
        });

        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.FindByEmailAsync("new@example.com"))!.EmailConfirmed.Should().BeTrue("the reset link reached the mailbox");
        (await client.PostAsJsonAsync("/spark/auth/login", new { email = "new@example.com", password = "Brand-new-password-2" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Registration_requires_a_user_name_that_is_not_an_email_address()
    {
        // G-Q22: the user name is the public handle; registration used to set it to the email.
        await using var host = await AccountTestHost.StartAsync(Store);
        using var client = host.Client();

        var missing = await client.PostAsJsonAsync("/spark/auth/register", new { email = "a@example.com", password = AccountTestHost.Password });
        var blank = await client.PostAsJsonAsync("/spark/auth/register", new { email = "a@example.com", password = AccountTestHost.Password, userName = "  " });
        var emailShaped = await client.PostAsJsonAsync("/spark/auth/register", new { email = "a@example.com", password = AccountTestHost.Password, userName = "a@example.com" });
        var accepted = await client.PostAsJsonAsync("/spark/auth/register", new { email = "a@example.com", password = AccountTestHost.Password, userName = " alice " });

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        emailShaped.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await emailShaped.Content.ReadAsStringAsync()).Should().Contain(SparkUserNameValidator<SparkUser>.ErrorCode);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.FindByEmailAsync("a@example.com"))!.UserName.Should().Be("alice");
    }

    [Fact]
    public async Task RequireConfirmedEmail_refuses_sign_in_until_the_mailed_link_is_posted_back()
    {
        await using var host = await AccountTestHost.StartAsync(Store, configure: o => o.RequireConfirmedEmail = true);
        using var client = host.Client();

        await client.PostAsJsonAsync("/spark/auth/register", new { email = "gate@example.com", password = AccountTestHost.Password, userName = "gatekeeper" });
        var before = await client.PostAsJsonAsync("/spark/auth/login", new { email = "gate@example.com", password = AccountTestHost.Password, userName = "gatekeeper" });

        var link = host.Mail.Sent.Single();
        var badCode = await client.PostAsJsonAsync("/spark/auth/confirm-email", new { userId = link.Query("userId"), code = "bm90LWEtdG9rZW4" });
        var confirm = await client.PostAsJsonAsync("/spark/auth/confirm-email", new { userId = link.Query("userId"), code = link.Query("code") });
        var after = await client.PostAsJsonAsync("/spark/auth/login", new { email = "gate@example.com", password = AccountTestHost.Password, userName = "gatekeeper" });

        before.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await before.Content.ReadAsStringAsync()).Should().Contain("NotAllowed");
        badCode.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_a_public_base_url_outside_Development_no_link_is_mailed()
    {
        // Password-reset poisoning: the Host header of an anonymous request is the attacker's.
        await using var host = await AccountTestHost.StartAsync(Store, publicBaseUrl: false);
        await host.CreateUserAsync("victim", "victim@example.com");
        using var client = host.Client();

        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/auth/forgotPassword")
        {
            Content = JsonContent.Create(new { email = "victim@example.com" }),
        };
        request.Headers.Host = "attacker.example";
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the answer must not depend on whether a mail went out");
        host.Mail.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_email_change_is_mailed_to_the_new_address_and_completes_through_confirm_email()
    {
        await using var host = await AccountTestHost.StartAsync(Store, configure: o => o.EmailChange = SparkEmailChange.Enabled);
        await host.CreateUserAsync("mover", "before@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "before@example.com");

        var info = await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/info", cookie, new { newEmail = "after@example.com" });
        var mail = host.Mail.Sent.Single();
        var confirm = await client.PostAsJsonAsync("/spark/auth/confirm-email", new
        {
            userId = mail.Query("userId"),
            code = mail.Query("code"),
            changedEmail = mail.Query("changedEmail"),
        });

        info.StatusCode.Should().Be(HttpStatusCode.OK);
        mail.Email.Should().Be("after@example.com");
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = await host.FindByEmailAsync("after@example.com");
        moved.Should().NotBeNull();
        moved!.UserName.Should().Be("mover", "a chosen handle is not the email and stays");
    }

    [Fact]
    public async Task By_default_an_email_change_is_refused_and_no_mail_is_sent()
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        await host.CreateUserAsync("stayer", "before@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "before@example.com");

        var info = await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/info", cookie,
            new { newEmail = "after@example.com", oldPassword = AccountTestHost.Password, newPassword = "Another-horse-2" });

        info.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await info.Content.ReadAsStringAsync()).Should().Contain("EmailChangeDisabled");
        host.Mail.Sent.Should().BeEmpty();
        (await host.FindByEmailAsync("before@example.com")).Should().NotBeNull();
        // Refused as a whole: the password half of the same request did not run either.
        (await host.CookieSignInAsync(client, "before@example.com")).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task By_default_a_change_link_does_not_confirm_even_with_a_valid_token()
    {
        // A link minted while the option was on, then the option switched off: the token is genuine.
        await using var host = await AccountTestHost.StartAsync(Store);
        var user = await host.CreateUserAsync("linked", "before@example.com");
        var code = await host.WithScopeAsync(services =>
            services.GetRequiredService<UserManager<SparkUser>>().GenerateChangeEmailTokenAsync(user, "after@example.com"));
        using var client = host.Client();

        var confirm = await client.PostAsJsonAsync("/spark/auth/confirm-email", new
        {
            userId = user.Id,
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code)),
            changedEmail = "after@example.com",
        });

        confirm.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await host.FindByEmailAsync("after@example.com")).Should().BeNull();
    }

    // /me used to read the cookie's claims, a copy taken at sign-in: a renamed user kept seeing the
    // old name until signing in again. It reads the store now.
    [Fact]
    public async Task Me_reflects_the_stored_user_name_without_signing_in_again()
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        var user = await host.CreateUserAsync("before-rename", "me@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "me@example.com");

        await host.WithScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<SparkUser>>();
            var stored = await users.FindByIdAsync(user.Id!);
            (await users.SetUserNameAsync(stored!, "after-rename")).Succeeded.Should().BeTrue();
            return true;
        });

        var me = await AccountTestHost.SendAsync(client, HttpMethod.Get, "/spark/auth/me", cookie);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
        body.GetProperty("userName").GetString().Should().Be("after-rename");
        body.GetProperty("email").GetString().Should().Be("me@example.com");
        body.GetProperty("roles").GetArrayLength().Should().Be(0, "roles come from the store too; this user has none");
    }

    [Fact]
    public async Task Me_answers_an_anonymous_caller_as_not_authenticated()
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        using var client = host.Client();

        var me = await AccountTestHost.SendAsync(client, HttpMethod.Get, "/spark/auth/me");
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Confirming_a_plain_email_still_works_with_email_change_disabled()
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        var user = await host.CreateUserAsync("fresh", "fresh@example.com", confirmed: false);
        var code = await host.WithScopeAsync(services =>
            services.GetRequiredService<UserManager<SparkUser>>().GenerateEmailConfirmationTokenAsync(user));
        using var client = host.Client();

        var confirm = await client.PostAsJsonAsync("/spark/auth/confirm-email", new
        {
            userId = user.Id,
            code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(code)),
        });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region D16 — account pages

    [Fact]
    public async Task A_social_only_account_sets_its_first_password_and_then_needs_it_to_change_it()
    {
        await using var host = await AccountTestHost.StartAsync(Store, endpoints: e =>
            e.MapPost("/test/sign-in", async (string email, SignInManager<SparkUser> signIn, UserManager<SparkUser> users) =>
            {
                await signIn.SignInAsync((await users.FindByEmailAsync(email))!, isPersistent: false);
                return Microsoft.AspNetCore.Http.Results.Ok();
            }));
        await host.CreateUserAsync("social", "social@example.com", password: null);
        // No password to sign in with, so issue the cookie the way an external login would.
        using var client = host.Client();
        var cookie = await SignInWithoutPasswordAsync(host, client, "social@example.com");

        var set = await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/password", cookie, new { newPassword = "First-password-1" });
        var changeWithout = await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/password", AccountTestHost.CookieHeader(set) is { Length: > 0 } c ? c : cookie, new { newPassword = "Second-password-2" });

        set.StatusCode.Should().Be(HttpStatusCode.OK);
        changeWithout.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await changeWithout.Content.ReadAsStringAsync()).Should().Contain("CurrentPasswordRequired");
        (await client.PostAsJsonAsync("/spark/auth/login", new { email = "social@example.com", password = "First-password-1" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class DisplayNameContributor : ISparkProfileContributor<SparkUser>
    {
        public IReadOnlyCollection<string> Fields => ["displayName"];

        public ValueTask<IReadOnlyDictionary<string, object?>> GetAsync(SparkUser user, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["displayName"] = user.Claims.FirstOrDefault(c => c.ClaimType == "display_name")?.ClaimValue,
            });

        public ValueTask ValidateAsync(SparkUser user, IReadOnlyDictionary<string, JsonElement> values, SparkProfileErrors errors, CancellationToken cancellationToken)
        {
            if (values.TryGetValue("displayName", out var value) && (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 20))
                errors.Add("displayName", "At most 20 characters.");
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplyAsync(SparkUser user, IReadOnlyDictionary<string, JsonElement> values, CancellationToken cancellationToken)
        {
            user.Claims.RemoveAll(c => c.ClaimType == "display_name");
            user.Claims.Add(new SparkUserClaim { ClaimType = "display_name", ClaimValue = values["displayName"].GetString()! });
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_profile_saves_contributed_fields_and_the_user_name_all_or_nothing()
    {
        await using var host = await AccountTestHost.StartAsync(Store,
            services: s => s.AddScoped<ISparkProfileContributor<SparkUser>, DisplayNameContributor>());
        await host.CreateUserAsync("taken", "taken@example.com");
        await host.CreateUserAsync("profiler", "profile@example.com");
        await WaitForIndexesAsync();
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "profile@example.com");

        async Task<HttpResponseMessage> Post(object body) => await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/profile", cookie, body);

        var unknown = await Post(new { fields = new { shoeSize = 44 } });
        var invalid = await Post(new { userName = "renamed", fields = new { displayName = new string('x', 21) } });
        var emailShaped = await Post(new { userName = "someone@else.example" });
        var clash = await Post(new { userName = "taken" });
        var saved = await Post(new { userName = "renamed", fields = new { displayName = "Jane" } });

        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        emailShaped.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await emailShaped.Content.ReadAsStringAsync()).Should().Contain(SparkUserNameValidator<SparkUser>.ErrorCode);
        clash.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await saved.Content.ReadAsStringAsync();
        body.Should().Contain("\"userName\":\"renamed\"").And.Contain("\"displayName\":\"Jane\"");
        (await host.FindByEmailAsync("profile@example.com"))!.UserName.Should().Be("renamed");
    }

    [Fact]
    public async Task The_profile_reads_and_writes_the_preferred_mail_culture()
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        await host.CreateUserAsync("polyglot", "polyglot@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "polyglot@example.com");

        async Task<HttpResponseMessage> Post(object body) => await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/profile", cookie, body);
        async Task<string?> Stored() => (await host.FindByEmailAsync("polyglot@example.com"))!.PreferredCulture;

        var initial = await AccountTestHost.SendAsync(client, HttpMethod.Get, "/spark/auth/manage/profile", cookie);
        using (var json = JsonDocument.Parse(await initial.Content.ReadAsStringAsync()))
            json.RootElement.GetProperty("preferredCulture").ValueKind.Should().Be(JsonValueKind.Null);

        var set = await Post(new { preferredCulture = "nl-be" });
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        (await set.Content.ReadAsStringAsync()).Should().Contain("\"preferredCulture\":\"nl-BE\"", "stored in its canonical casing");
        (await Stored()).Should().Be("nl-BE");

        var invalid = await Post(new { preferredCulture = "xx-not-a-culture" });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("PreferredCulture");
        (await Stored()).Should().Be("nl-BE", "a refused save writes nothing");

        (await Post(new { userName = "polyglot" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Stored()).Should().Be("nl-BE", "an absent field keeps the value");

        (await Post(new { preferredCulture = "" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Stored()).Should().BeNull("empty clears it, back to Spark:Mail:DefaultCulture");

        await Post(new { preferredCulture = "fr" });
        (await Post(new { preferredCulture = (string?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Stored()).Should().BeNull("an explicit null clears it too");
    }

    [Theory]
    [InlineData("en", true, "en")]
    [InlineData("nl-be", true, "nl-BE")]
    [InlineData(" fr ", true, "fr")]
    [InlineData("", true, null)]
    [InlineData(null, true, null)]
    [InlineData("xx-not-a-culture", false, null)]
    [InlineData("en-US-x-custom-private-use-extension-longer-than-any-real-locale-name-could-be-at-all-padding", false, null)]
    public void A_preferred_culture_must_be_a_predefined_culture_name(string? posted, bool valid, string? stored)
    {
        MintPlayer.Spark.Authorization.Extensions.SparkAccountEndpoints.TryNormalizeCulture(posted, out var culture).Should().Be(valid);
        culture.Should().Be(stored);
    }

    [Fact]
    public async Task The_authenticator_uri_carries_the_key_and_a_server_rendered_svg_and_is_never_cached()
    {
        await using var host = await AccountTestHost.StartAsync(Store, configure: o => o.AuthenticatorIssuer = "Spark Tests");
        await host.CreateUserAsync("totp", "totp@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "totp@example.com");

        var before = await AccountTestHost.SendAsync(client, HttpMethod.Get, "/spark/auth/manage/2fa/authenticator-uri", cookie);
        (await AccountTestHost.SendAsync(client, HttpMethod.Post, "/spark/auth/manage/2fa", cookie, new { })).EnsureSuccessStatusCode();
        var after = await AccountTestHost.SendAsync(client, HttpMethod.Get, "/spark/auth/manage/2fa/authenticator-uri", cookie);

        before.StatusCode.Should().Be(HttpStatusCode.Conflict, "a GET never creates the key");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        after.Headers.CacheControl!.NoStore.Should().BeTrue();

        using var json = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        var key = json.RootElement.GetProperty("sharedKey").GetString()!;
        json.RootElement.GetProperty("authenticatorUri").GetString().Should()
            .Be($"otpauth://totp/Spark%20Tests:totp%40example.com?secret={key}&issuer=Spark%20Tests&digits=6");
        json.RootElement.GetProperty("qrCodeSvg").GetString().Should().Contain("<svg");
    }

    #endregion

    #region D8 — personal data and deletion

    private sealed class NotesContributor : ISparkPersonalDataContributor<SparkUser>
    {
        public string Name => "notes";
        public Task<object?> GetPersonalDataAsync(SparkUser user, CancellationToken cancellationToken)
            => Task.FromResult<object?>(new[] { $"note of {user.UserName}" });
    }

    private sealed class RecordingDeletionHandler(List<string> calls, bool fail) : ISparkAccountDeletionHandler<SparkUser>
    {
        public Task OnDeletingAccountAsync(SparkUser user, CancellationToken cancellationToken)
        {
            calls.Add(user.Id!);
            return fail ? throw new InvalidOperationException("content store unavailable") : Task.CompletedTask;
        }
    }

    private sealed class RecordingDeletedHandler(List<string> calls, Func<SparkUser, Task<bool>>? stillStored = null, bool fail = false) : ISparkAccountDeletedHandler<SparkUser>
    {
        public async Task OnAccountDeletedAsync(SparkUser user, CancellationToken cancellationToken)
        {
            calls.Add("deleted:" + user.Id + (stillStored is not null && await stillStored(user) ? ":still-stored" : string.Empty));
            if (fail)
                throw new InvalidOperationException("mail queue unavailable");
        }
    }

    [Fact]
    public async Task The_after_deletion_hook_runs_once_the_store_deleted_the_account_and_never_before()
    {
        var calls = new List<string>();
        AccountTestHost? started = null;
        await using var host = started = await AccountTestHost.StartAsync(Store,
            services: s =>
            {
                s.AddScoped<ISparkAccountDeletionHandler<SparkUser>>(_ => new RecordingDeletionHandler(calls, fail: false));
                s.AddScoped<ISparkAccountDeletedHandler<SparkUser>>(_ => new RecordingDeletedHandler(calls,
                    async u => await started!.FindByEmailAsync(u.Email!) is not null));
            });
        var user = await host.CreateUserAsync("goodbye", "goodbye@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "goodbye@example.com");

        var deleted = await AccountTestHost.SendAsync(client, HttpMethod.Delete, "/spark/auth/manage/account", cookie, new { password = AccountTestHost.Password });

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        calls.Should().Equal(user.Id!, "deleted:" + user.Id);
    }

    [Fact]
    public async Task No_after_deletion_hook_runs_when_the_deletion_stopped_and_a_failing_one_does_not_undo_it()
    {
        var calls = new List<string>();
        await using (var stopped = await AccountTestHost.StartAsync(Store,
            services: s =>
            {
                s.AddScoped<ISparkAccountDeletionHandler<SparkUser>>(_ => new RecordingDeletionHandler(calls, fail: true));
                s.AddScoped<ISparkAccountDeletedHandler<SparkUser>>(_ => new RecordingDeletedHandler(calls));
            }))
        {
            await stopped.CreateUserAsync("stays", "stays@example.com");
            using var client = stopped.Client();
            var cookie = await stopped.CookieSignInAsync(client, "stays@example.com");
            (await AccountTestHost.SendAsync(client, HttpMethod.Delete, "/spark/auth/manage/account", cookie, new { password = AccountTestHost.Password }))
                .StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            calls.Should().ContainSingle().Which.Should().NotStartWith("deleted:", "the account is intact, so nothing may say goodbye");
        }

        calls.Clear();
        await using var failing = await AccountTestHost.StartAsync(Store,
            services: s => s.AddScoped<ISparkAccountDeletedHandler<SparkUser>>(_ => new RecordingDeletedHandler(calls, fail: true)));
        await failing.CreateUserAsync("gone", "gone@example.com");
        using var goneClient = failing.Client();
        var goneCookie = await failing.CookieSignInAsync(goneClient, "gone@example.com");

        (await AccountTestHost.SendAsync(goneClient, HttpMethod.Delete, "/spark/auth/manage/account", goneCookie, new { password = AccountTestHost.Password }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "the account is already gone; a retry could only 404");
        calls.Should().ContainSingle();
        (await failing.FindByEmailAsync("gone@example.com")).Should().BeNull();
    }

    [Fact]
    public async Task Personal_data_holds_the_account_and_contributions_and_never_a_secret()
    {
        await using var host = await AccountTestHost.StartAsync(Store,
            services: s => s.AddScoped<ISparkPersonalDataContributor<SparkUser>, NotesContributor>());
        await host.CreateUserAsync("exporter", "export@example.com");
        await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var user = (await users.FindByEmailAsync("export@example.com"))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetAuthenticationTokenAsync(user, "GitHub", "access_token", "gho_secret-token");
            return true;
        });
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "export@example.com");

        var response = await AccountTestHost.SendAsync(client, HttpMethod.Get, "/spark/auth/manage/personal-data", cookie);
        var body = await response.Content.ReadAsStringAsync();
        var raw = (await host.FindByEmailAsync("export@example.com"))!;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"email\":\"export@example.com\"").And.Contain("\"notes\":[\"note of exporter\"]");
        body.Should().NotContain(raw.PasswordHash!).And.NotContain("gho_secret-token").And.NotContain(raw.AuthenticatorKey!)
            .And.NotContain("sdp1:").And.NotContain("securityStamp");
        body.Should().Contain("\"hasPassword\":true", "the deletion form asks for the password only when there is one");
    }

    [Fact]
    public async Task Personal_data_says_when_the_account_has_no_password()
    {
        await using var host = await AccountTestHost.StartAsync(Store);
        await host.CreateUserAsync("nopass", "nopass@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "nopass@example.com");
        await host.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<SparkUser>>();
            var user = (await users.FindByEmailAsync("nopass@example.com"))!;
            (await users.RemovePasswordAsync(user)).Succeeded.Should().BeTrue();
            return true;
        });

        var response = await AccountTestHost.SendAsync(client, HttpMethod.Get, "/spark/auth/manage/personal-data", cookie);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"hasPassword\":false");
    }

    [Fact]
    public async Task Deleting_with_the_password_runs_every_handler_then_removes_the_account_and_frees_the_email()
    {
        var calls = new List<string>();
        await using var host = await AccountTestHost.StartAsync(Store,
            services: s => s.AddScoped<ISparkAccountDeletionHandler<SparkUser>>(_ => new RecordingDeletionHandler(calls, fail: false)));
        var user = await host.CreateUserAsync("leaver", "leaver@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "leaver@example.com");

        var wrong = await AccountTestHost.SendAsync(client, HttpMethod.Delete, "/spark/auth/manage/account", cookie, new { password = "not-it" });
        var deleted = await AccountTestHost.SendAsync(client, HttpMethod.Delete, "/spark/auth/manage/account", cookie, new { password = AccountTestHost.Password });

        wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        calls.Should().Equal(user.Id!);
        (await host.FindByEmailAsync("leaver@example.com")).Should().BeNull();
        (await client.PostAsJsonAsync("/spark/auth/register", new { email = "leaver@example.com", password = AccountTestHost.Password, userName = "leaver-again" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the email reservation was released");
    }

    [Fact]
    public async Task Deleting_without_a_password_needs_a_recent_sign_in()
    {
        await using var fresh = await AccountTestHost.StartAsync(Store);
        await fresh.CreateUserAsync("recent", "recent@example.com");
        using var client = fresh.Client();
        var cookie = await fresh.CookieSignInAsync(client, "recent@example.com");

        (await AccountTestHost.SendAsync(client, HttpMethod.Delete, "/spark/auth/manage/account", cookie))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var stale = await AccountTestHost.StartAsync(Store, configure: o => o.ReauthenticationMaxAge = TimeSpan.Zero);
        await stale.CreateUserAsync("stale", "stale@example.com");
        using var staleClient = stale.Client();
        var staleCookie = await stale.CookieSignInAsync(staleClient, "stale@example.com");

        var refused = await AccountTestHost.SendAsync(staleClient, HttpMethod.Delete, "/spark/auth/manage/account", staleCookie);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("reauthentication_required");
        (await stale.FindByEmailAsync("stale@example.com")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_failing_deletion_handler_leaves_the_account_intact()
    {
        var calls = new List<string>();
        await using var host = await AccountTestHost.StartAsync(Store,
            services: s => s.AddScoped<ISparkAccountDeletionHandler<SparkUser>>(_ => new RecordingDeletionHandler(calls, fail: true)));
        await host.CreateUserAsync("keeper", "keeper@example.com");
        using var client = host.Client();
        var cookie = await host.CookieSignInAsync(client, "keeper@example.com");

        var response = await AccountTestHost.SendAsync(client, HttpMethod.Delete, "/spark/auth/manage/account", cookie, new { password = AccountTestHost.Password });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        calls.Should().ContainSingle();
        (await host.FindByEmailAsync("keeper@example.com")).Should().NotBeNull("a retry must find the account");
    }

    #endregion

    /// <summary>Issues the application cookie for a password-less account, as the external-login callback would.</summary>
    private static async Task<string> SignInWithoutPasswordAsync(AccountTestHost host, HttpClient client, string email)
    {
        var response = await client.PostAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}", null);
        response.EnsureSuccessStatusCode();
        return AccountTestHost.CookieHeader(response);
    }
}
