using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Identity;

/// <summary>
/// Pins the contract of the RavenDB-backed <see cref="UserStore{TUser}"/>. The store implements
/// 14 ASP.NET Identity store interfaces and uses RavenDB compare/exchange operations to enforce
/// cluster-safe email uniqueness — a regression in the email-reservation flow silently breaks
/// registration across the entire authentication stack.
/// </summary>
public class UserStoreTests : SparkTestDriver
{
    private UserStore<SparkUser> CreateStore() => new(Store);

    private static SparkUser NewUser(string? userName = "alice", string? email = null)
    {
        var u = new SparkUser
        {
            UserName = userName,
            NormalizedUserName = userName?.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email?.ToUpperInvariant()
        };
        return u;
    }

    #region CRUD + email reservation

    [Fact]
    public async Task CreateAsync_persists_user_with_no_email()
    {
        var user = NewUser(email: null);

        var creating = CreateStore();
        var result = await creating.CreateAsync(user, CancellationToken.None);
        creating.Dispose();

        result.Should().Be(IdentityResult.Success);
        user.Id.Should().NotBeNullOrEmpty();

        using var reading = CreateStore();
        var loaded = await reading.FindByIdAsync(user.Id!, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.UserName.Should().Be("alice");
    }

    [Fact]
    public async Task CreateAsync_succeeds_for_first_user_with_a_given_email()
    {
        var user = NewUser(email: "alice@example.com");

        using var store = CreateStore();
        var result = await store.CreateAsync(user, CancellationToken.None);

        result.Should().Be(IdentityResult.Success);
    }

    [Fact]
    public async Task CreateAsync_returns_DuplicateEmail_for_a_second_user_with_the_same_email()
    {
        using var first = CreateStore();
        await first.CreateAsync(NewUser("alice", "shared@example.com"), CancellationToken.None);

        using var second = CreateStore();
        var result = await second.CreateAsync(NewUser("bob", "shared@example.com"), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("DuplicateEmail");
    }

    [Fact]
    public async Task UpdateAsync_persists_generic_field_changes()
    {
        var user = NewUser("alice");
        var creating = CreateStore();
        await creating.CreateAsync(user, CancellationToken.None);
        creating.Dispose();

        var updating = CreateStore();
        var loaded = await updating.FindByIdAsync(user.Id!, CancellationToken.None);
        loaded!.UserName = "alice-renamed";
        loaded.NormalizedUserName = "ALICE-RENAMED";
        var result = await updating.UpdateAsync(loaded, CancellationToken.None);
        updating.Dispose();

        result.Should().Be(IdentityResult.Success);

        using var verifying = CreateStore();
        var verified = await verifying.FindByIdAsync(user.Id!, CancellationToken.None);
        verified!.UserName.Should().Be("alice-renamed");
    }

    [Fact]
    public async Task UpdateAsync_with_email_change_releases_old_reservation_and_creates_new()
    {
        var user = NewUser("alice", "old@example.com");
        var creating = CreateStore();
        await creating.CreateAsync(user, CancellationToken.None);
        creating.Dispose();

        var updating = CreateStore();
        var loaded = await updating.FindByIdAsync(user.Id!, CancellationToken.None);
        loaded!.Email = "new@example.com";
        loaded.NormalizedEmail = "NEW@EXAMPLE.COM";
        var result = await updating.UpdateAsync(loaded, CancellationToken.None);
        updating.Dispose();

        result.Should().Be(IdentityResult.Success);

        // Old email reservation must be free again — a new user can claim it.
        using var newOwner = CreateStore();
        var rebound = await newOwner.CreateAsync(NewUser("bob", "old@example.com"), CancellationToken.None);
        rebound.Should().Be(IdentityResult.Success);
    }

    [Fact]
    public async Task UpdateAsync_returns_DuplicateEmail_when_new_email_is_already_taken()
    {
        using var firstStore = CreateStore();
        await firstStore.CreateAsync(NewUser("alice", "alice@example.com"), CancellationToken.None);

        var bob = NewUser("bob", "bob@example.com");
        using var secondStore = CreateStore();
        await secondStore.CreateAsync(bob, CancellationToken.None);

        using var updating = CreateStore();
        var loadedBob = await updating.FindByIdAsync(bob.Id!, CancellationToken.None);
        loadedBob!.Email = "alice@example.com";
        loadedBob.NormalizedEmail = "ALICE@EXAMPLE.COM";

        var result = await updating.UpdateAsync(loadedBob, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("DuplicateEmail");
    }

    [Fact]
    public async Task DeleteAsync_removes_user_and_releases_email_reservation()
    {
        var user = NewUser("alice", "alice@example.com");
        var creating = CreateStore();
        await creating.CreateAsync(user, CancellationToken.None);
        creating.Dispose();

        var deleting = CreateStore();
        var loaded = await deleting.FindByIdAsync(user.Id!, CancellationToken.None);
        var result = await deleting.DeleteAsync(loaded!, CancellationToken.None);
        deleting.Dispose();

        result.Should().Be(IdentityResult.Success);

        using var verifying = CreateStore();
        (await verifying.FindByIdAsync(user.Id!, CancellationToken.None)).Should().BeNull();

        // Reservation released — same email is reusable.
        using var reUser = CreateStore();
        var rebound = await reUser.CreateAsync(NewUser("alice2", "alice@example.com"), CancellationToken.None);
        rebound.Should().Be(IdentityResult.Success);
    }

    [Fact]
    public async Task FindByIdAsync_returns_null_when_missing()
    {
        using var store = CreateStore();
        (await store.FindByIdAsync("SparkUsers/nope", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task FindByNameAsync_resolves_user_by_NormalizedUserName()
    {
        var user = NewUser("alice");
        var creating = CreateStore();
        await creating.CreateAsync(user, CancellationToken.None);
        creating.Dispose();

        WaitForIndexing(Store);

        using var reading = CreateStore();
        var found = await reading.FindByNameAsync("ALICE", CancellationToken.None);

        found.Should().NotBeNull();
        found!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task FindByEmailAsync_resolves_via_compare_exchange()
    {
        var user = NewUser("alice", "alice@example.com");
        var creating = CreateStore();
        await creating.CreateAsync(user, CancellationToken.None);
        creating.Dispose();

        using var reading = CreateStore();
        var found = await reading.FindByEmailAsync("ALICE@EXAMPLE.COM", CancellationToken.None);

        found.Should().NotBeNull();
        found!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task FindByEmailAsync_returns_null_when_reservation_missing()
    {
        using var store = CreateStore();
        (await store.FindByEmailAsync("NOPE@EXAMPLE.COM", CancellationToken.None)).Should().BeNull();
    }

    #endregion

    #region IUserStore — accessors

    [Fact]
    public async Task User_id_and_username_accessors_round_trip()
    {
        using var store = CreateStore();
        var user = new SparkUser { Id = "u/1", UserName = "alice", NormalizedUserName = "ALICE" };

        (await store.GetUserIdAsync(user, CancellationToken.None)).Should().Be("u/1");
        (await store.GetUserNameAsync(user, CancellationToken.None)).Should().Be("alice");
        (await store.GetNormalizedUserNameAsync(user, CancellationToken.None)).Should().Be("ALICE");

        await store.SetUserNameAsync(user, "bob", CancellationToken.None);
        await store.SetNormalizedUserNameAsync(user, "BOB", CancellationToken.None);

        user.UserName.Should().Be("bob");
        user.NormalizedUserName.Should().Be("BOB");
    }

    [Fact]
    public async Task GetUserIdAsync_returns_empty_when_id_is_null()
    {
        using var store = CreateStore();
        (await store.GetUserIdAsync(new SparkUser(), CancellationToken.None)).Should().BeEmpty();
    }

    #endregion

    #region IUserPasswordStore + IUserEmailStore + IUserSecurityStampStore

    [Fact]
    public async Task Password_hash_set_get_and_HasPassword()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        (await store.HasPasswordAsync(user, CancellationToken.None)).Should().BeFalse();

        await store.SetPasswordHashAsync(user, "hash", CancellationToken.None);

        (await store.GetPasswordHashAsync(user, CancellationToken.None)).Should().Be("hash");
        (await store.HasPasswordAsync(user, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Email_and_confirmed_accessors_round_trip()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.SetEmailAsync(user, "alice@example.com", CancellationToken.None);
        await store.SetNormalizedEmailAsync(user, "ALICE@EXAMPLE.COM", CancellationToken.None);
        await store.SetEmailConfirmedAsync(user, true, CancellationToken.None);

        (await store.GetEmailAsync(user, CancellationToken.None)).Should().Be("alice@example.com");
        (await store.GetNormalizedEmailAsync(user, CancellationToken.None)).Should().Be("ALICE@EXAMPLE.COM");
        (await store.GetEmailConfirmedAsync(user, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SecurityStamp_accessors_round_trip()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.SetSecurityStampAsync(user, "stamp-1", CancellationToken.None);

        (await store.GetSecurityStampAsync(user, CancellationToken.None)).Should().Be("stamp-1");
    }

    #endregion

    #region IUserRoleStore

    [Fact]
    public async Task Role_membership_is_case_insensitive_and_idempotent()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.AddToRoleAsync(user, "Admins", CancellationToken.None);
        await store.AddToRoleAsync(user, "ADMINS", CancellationToken.None); // duplicate — must be idempotent

        (await store.GetRolesAsync(user, CancellationToken.None)).Should().ContainSingle().Which.Should().Be("Admins");
        (await store.IsInRoleAsync(user, "admins", CancellationToken.None)).Should().BeTrue();

        await store.RemoveFromRoleAsync(user, "ADMINS", CancellationToken.None);

        (await store.GetRolesAsync(user, CancellationToken.None)).Should().BeEmpty();
        (await store.IsInRoleAsync(user, "admins", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task GetUsersInRoleAsync_finds_users_by_role()
    {
        var alice = NewUser("alice");
        alice.Roles.Add("Admins");
        var bob = NewUser("bob");

        var creating = CreateStore();
        await creating.CreateAsync(alice, CancellationToken.None);
        await creating.CreateAsync(bob, CancellationToken.None);
        creating.Dispose();

        WaitForIndexing(Store);

        using var reading = CreateStore();
        var users = await reading.GetUsersInRoleAsync("Admins", CancellationToken.None);

        users.Should().ContainSingle().Which.UserName.Should().Be("alice");
    }

    #endregion

    #region IUserClaimStore

    [Fact]
    public async Task Claims_add_get_replace_and_remove_round_trip()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.AddClaimsAsync(user, [new Claim("scope", "read"), new Claim("scope", "write")], CancellationToken.None);

        var claims = await store.GetClaimsAsync(user, CancellationToken.None);
        claims.Should().HaveCount(2);

        await store.ReplaceClaimAsync(user, new Claim("scope", "read"), new Claim("scope", "admin"), CancellationToken.None);

        var afterReplace = await store.GetClaimsAsync(user, CancellationToken.None);
        afterReplace.Should().Contain(c => c.Type == "scope" && c.Value == "admin");
        afterReplace.Should().NotContain(c => c.Type == "scope" && c.Value == "read");

        await store.RemoveClaimsAsync(user, [new Claim("scope", "admin")], CancellationToken.None);

        var afterRemove = await store.GetClaimsAsync(user, CancellationToken.None);
        afterRemove.Should().ContainSingle().Which.Value.Should().Be("write");
    }

    [Fact]
    public async Task GetUsersForClaimAsync_finds_users_by_claim()
    {
        var alice = NewUser("alice");
        alice.Claims.Add(new SparkUserClaim { ClaimType = "scope", ClaimValue = "admin" });
        var bob = NewUser("bob");
        bob.Claims.Add(new SparkUserClaim { ClaimType = "scope", ClaimValue = "user" });

        var creating = CreateStore();
        await creating.CreateAsync(alice, CancellationToken.None);
        await creating.CreateAsync(bob, CancellationToken.None);
        creating.Dispose();

        WaitForIndexing(Store);

        using var reading = CreateStore();
        var users = await reading.GetUsersForClaimAsync(new Claim("scope", "admin"), CancellationToken.None);

        users.Should().ContainSingle().Which.UserName.Should().Be("alice");
    }

    #endregion

    #region IUserLockoutStore + IUserTwoFactorStore + IUserAuthenticatorKeyStore

    [Fact]
    public async Task Lockout_end_and_enabled_accessors_round_trip()
    {
        using var store = CreateStore();
        var user = new SparkUser();
        var until = DateTimeOffset.UtcNow.AddMinutes(5);

        await store.SetLockoutEndDateAsync(user, until, CancellationToken.None);
        await store.SetLockoutEnabledAsync(user, true, CancellationToken.None);

        (await store.GetLockoutEndDateAsync(user, CancellationToken.None)).Should().Be(until);
        (await store.GetLockoutEnabledAsync(user, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task AccessFailedCount_increments_and_resets()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        (await store.IncrementAccessFailedCountAsync(user, CancellationToken.None)).Should().Be(1);
        (await store.IncrementAccessFailedCountAsync(user, CancellationToken.None)).Should().Be(2);
        (await store.GetAccessFailedCountAsync(user, CancellationToken.None)).Should().Be(2);

        await store.ResetAccessFailedCountAsync(user, CancellationToken.None);

        (await store.GetAccessFailedCountAsync(user, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task TwoFactor_and_authenticator_key_accessors_round_trip()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.SetTwoFactorEnabledAsync(user, true, CancellationToken.None);
        await store.SetAuthenticatorKeyAsync(user, "AUTH-KEY", CancellationToken.None);

        (await store.GetTwoFactorEnabledAsync(user, CancellationToken.None)).Should().BeTrue();
        (await store.GetAuthenticatorKeyAsync(user, CancellationToken.None)).Should().Be("AUTH-KEY");
    }

    #endregion

    #region IUserTwoFactorRecoveryCodeStore

    [Fact]
    public async Task RecoveryCodes_replace_count_and_redeem()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.ReplaceCodesAsync(user, ["a", "b", "c"], CancellationToken.None);
        (await store.CountCodesAsync(user, CancellationToken.None)).Should().Be(3);

        (await store.RedeemCodeAsync(user, "b", CancellationToken.None)).Should().BeTrue();
        (await store.RedeemCodeAsync(user, "missing", CancellationToken.None)).Should().BeFalse();
        (await store.CountCodesAsync(user, CancellationToken.None)).Should().Be(2);
    }

    #endregion

    #region IUserLoginStore

    [Fact]
    public async Task Logins_add_get_remove_round_trip()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.AddLoginAsync(user, new UserLoginInfo("github", "12345", "GitHub"), CancellationToken.None);
        await store.AddLoginAsync(user, new UserLoginInfo("google", "abcde", "Google"), CancellationToken.None);

        var logins = await store.GetLoginsAsync(user, CancellationToken.None);
        logins.Should().HaveCount(2);

        await store.RemoveLoginAsync(user, "github", "12345", CancellationToken.None);

        var afterRemove = await store.GetLoginsAsync(user, CancellationToken.None);
        afterRemove.Should().ContainSingle().Which.LoginProvider.Should().Be("google");
    }

    [Fact]
    public async Task FindByLoginAsync_resolves_user_by_provider_and_key()
    {
        var alice = NewUser("alice");
        alice.Logins.Add(new SparkUserLogin { LoginProvider = "github", ProviderKey = "12345", ProviderDisplayName = "GitHub" });
        var bob = NewUser("bob");
        bob.Logins.Add(new SparkUserLogin { LoginProvider = "google", ProviderKey = "12345" });

        var creating = CreateStore();
        await creating.CreateAsync(alice, CancellationToken.None);
        await creating.CreateAsync(bob, CancellationToken.None);
        creating.Dispose();

        WaitForIndexing(Store);

        using var reading = CreateStore();
        var found = await reading.FindByLoginAsync("github", "12345", CancellationToken.None);

        found.Should().NotBeNull();
        found!.UserName.Should().Be("alice");
    }

    #endregion

    #region IUserAuthenticationTokenStore

    [Fact]
    public async Task Tokens_set_new_update_existing_get_and_remove()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.SetTokenAsync(user, "github", "access", "v1", CancellationToken.None);
        (await store.GetTokenAsync(user, "github", "access", CancellationToken.None)).Should().Be("v1");

        // Updating the existing token must not duplicate.
        await store.SetTokenAsync(user, "github", "access", "v2", CancellationToken.None);
        (await store.GetTokenAsync(user, "github", "access", CancellationToken.None)).Should().Be("v2");
        user.Tokens.Should().ContainSingle();

        await store.RemoveTokenAsync(user, "github", "access", CancellationToken.None);
        (await store.GetTokenAsync(user, "github", "access", CancellationToken.None)).Should().BeNull();
    }

    #endregion

    #region IUserPhoneNumberStore

    [Fact]
    public async Task Phone_number_and_confirmed_accessors_round_trip()
    {
        using var store = CreateStore();
        var user = new SparkUser();

        await store.SetPhoneNumberAsync(user, "+15551234", CancellationToken.None);
        await store.SetPhoneNumberConfirmedAsync(user, true, CancellationToken.None);

        (await store.GetPhoneNumberAsync(user, CancellationToken.None)).Should().Be("+15551234");
        (await store.GetPhoneNumberConfirmedAsync(user, CancellationToken.None)).Should().BeTrue();
    }

    #endregion

    #region IQueryableUserStore

    [Fact]
    public async Task Users_queryable_returns_stored_users()
    {
        var creating = CreateStore();
        await creating.CreateAsync(NewUser("alice"), CancellationToken.None);
        await creating.CreateAsync(NewUser("bob"), CancellationToken.None);
        creating.Dispose();

        WaitForIndexing(Store);

        using var reading = CreateStore();
        var users = await reading.Users.ToListAsync();

        users.Select(u => u.UserName).Should().BeEquivalentTo(["alice", "bob"]);
    }

    #endregion

    #region Lifecycle

    [Fact]
    public void Dispose_is_idempotent()
    {
        var store = CreateStore();
        _ = store.Users; // force lazy session creation

        store.Dispose();
        var act = store.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Operations_after_Dispose_throw_ObjectDisposedException()
    {
        var store = CreateStore();
        store.Dispose();

        var act = async () => await store.CreateAsync(NewUser("late"), CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    #endregion
}
