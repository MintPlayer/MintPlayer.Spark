using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// The user store implementing all ASP.NET Core Identity store interfaces, on any Spark store (#536). Adapted from
/// RavenDB.Identity (MIT licensed) for MintPlayer.Spark's infrastructure. Emails and passkey credential ids are unique
/// through the store's reservations (<see cref="ISparkSession.Reserve"/>): compare exchange on RavenDB, under the same
/// keys as before, and a table written in the documents' transaction elsewhere.
/// </summary>
public partial class UserStore<TUser> :
    IUserStore<TUser>,
    IUserPasswordStore<TUser>,
    IUserEmailStore<TUser>,
    IUserSecurityStampStore<TUser>,
    IUserRoleStore<TUser>,
    IUserClaimStore<TUser>,
    IUserLockoutStore<TUser>,
    IUserTwoFactorStore<TUser>,
    IUserAuthenticatorKeyStore<TUser>,
    IUserTwoFactorRecoveryCodeStore<TUser>,
    IUserLoginStore<TUser>,
    IUserAuthenticationTokenStore<TUser>,
    IUserPhoneNumberStore<TUser>,
    IUserPasskeyStore<TUser>,
    IQueryableUserStore<TUser>
    where TUser : SparkUser
{
    private const string EmailReservationKeyPrefix = SparkUserReservations.EmailPrefix;
    private const string UserNameReservationKeyPrefix = SparkUserReservations.UserNamePrefix;
    private const string LoginReservationKeyPrefix = SparkUserReservations.LoginPrefix;

    [Inject] private readonly ISparkStore store;
    // All [Inject] fields live in this file: the generator emits one constructor per partial
    // declaration, so a field injected from UserStore.Secrets.cs produced a second, ambiguous one.
    [Inject] private readonly Microsoft.AspNetCore.DataProtection.IDataProtectionProvider? dataProtectionProvider;
    [Inject] private readonly Microsoft.Extensions.Logging.ILogger<UserStore<TUser>>? logger;
    [Inject] private readonly TimeProvider? timeProvider;
    // The groups security.json declares, whose names roles keep (see IUserRoleStore below). Absent without core's loader
    // (a bare AddSparkAuthentication, the store's unit tests): a role then keeps the name the store was handed.
    [Inject] private readonly ISecurityConfigurationLoader? securityConfiguration;
    private ISparkSession? session;
    private bool disposed;

    // Each user as this store handed it out: the lookup keys (email, user name, logins) it held, which UpdateAsync moves
    // the reservations from, and its version, which re-attaches it after a failed save dropped the session.
    private readonly Dictionary<string, Handout> handouts = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Handout(HashSet<string> Keys, string? Version);

    private ISparkSession Session
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (session != null)
                return session;

            session = store.OpenSession();

            // Identity's stores are read-modify-write over a whole user document: load the user,
            // mutate a field, save. Without optimistic concurrency the last save wins silently,
            // which loses updates that matter. The sharpest case is a recovery code — two
            // concurrent redemptions each load the user, each remove the same code from their own
            // copy, and both succeed, so a single-use two-factor bypass is spendable twice. The
            // failed-access counter behind lockout has the same shape.
            //
            // Identity models this outcome already: UpdateAsync answers ConcurrencyFailure, which
            // UserManager surfaces as a failed IdentityResult rather than an exception.
            session.OptimisticConcurrency = true;
            return session;
        }
    }

    /// <summary>Remembers <paramref name="user"/>'s lookup keys as stored, for <see cref="UpdateAsync"/>.</summary>
    private TUser? Loaded(TUser? user)
    {
        if (user?.Id is { } id && !handouts.ContainsKey(id))
            handouts[id] = new(LookupKeysOf(user), Session.GetVersion(user));
        return user;
    }

    /// <summary>
    /// Drops the session after a save failed on a taken key or a concurrent write. The session would keep the failed
    /// reservations and retry them on every later save; a fresh one starts clean, and <see cref="AttachAsync"/> brings
    /// back a user handed out before.
    /// </summary>
    private void ResetSession()
    {
        session?.Dispose();
        session = null;
    }

    /// <summary>Tracks <paramref name="user"/> again after <see cref="ResetSession"/>, at the version it was handed out with.</summary>
    private async Task AttachAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is { } id && !Session.IsLoaded(id) && handouts.TryGetValue(id, out var handout))
            await Session.StoreAsync(user, handout.Version, id, cancellationToken);
    }

    private IList<TUser> Loaded(IList<TUser> users)
    {
        foreach (var user in users)
            Loaded(user);
        return users;
    }

    /// <summary>The lookup keys <paramref name="user"/> holds in the store: as handed out, else read now.</summary>
    private async Task<HashSet<string>> StoredKeysAsync(TUser user, CancellationToken cancellationToken)
    {
        if (user.Id is null)
            return new(StringComparer.OrdinalIgnoreCase);
        if (handouts.TryGetValue(user.Id, out var handout))
            return handout.Keys;
        using var probe = store.OpenSession();
        return await probe.LoadAsync<TUser>(user.Id, cancellationToken) is { } stored
            ? LookupKeysOf(stored)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    #region IUserStore

    public async Task<IdentityResult> CreateAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // #460 item 6: when and how the account came to exist. A caller that knows the method (the
        // external-login callback) sets it; otherwise it is inferred from what the account holds at
        // creation. Existing accounts are backfilled by SparkUserBackfill.
        user.CreatedAtUtc ??= (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        user.RegistrationMethod ??= user.PasswordHash is not null
            ? SparkRegistrationMethods.Password
            : user.Logins.FirstOrDefault() is { } login
                ? SparkRegistrationMethods.External(login.LoginProvider)
                : SparkRegistrationMethods.Other;

        // StoreAsync assigns the id without writing anything, so the reservation names the real user
        // from the start.
        await Session.StoreAsync(user, cancellationToken);
        var userId = Session.GetId(user)!;
        user.Id ??= userId;

        // The email, the user name and every login are claimed with the document, in one save (#536 S6): a taken key
        // fails the save and nothing is written. Every passkey the new user already holds is claimed the same way.
        var keys = LookupKeysOf(user);
        foreach (var key in keys)
            Session.Reserve(key, userId);
        foreach (var passkey in user.Passkeys)
            Session.Reserve(PasskeyKeyFor(passkey.CredentialId), userId);

        try
        {
            await Session.SaveChangesAsync(cancellationToken);
        }
        catch (SparkReservationTakenException ex)
        {
            ResetSession();
            return ReservationTaken(user, ex);
        }

        handouts[userId] = new(keys, Session.GetVersion(user));
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // The reservations move with the email, the user name and the logins, in the same save: a new key is claimed
        // and an old one released only if the user document is written. A change of case keeps the same key.
        await AttachAsync(user, cancellationToken);
        var oldKeys = await StoredKeysAsync(user, cancellationToken);
        var newKeys = LookupKeysOf(user);
        if (user.Id is { } id)
        {
            foreach (var key in newKeys.Except(oldKeys, StringComparer.OrdinalIgnoreCase))
                Session.Reserve(key, id);
            foreach (var key in oldKeys.Except(newKeys, StringComparer.OrdinalIgnoreCase))
                Session.Release(key, id);
        }

        try
        {
            await Session.SaveChangesAsync(cancellationToken);
        }
        catch (SparkReservationTakenException ex)
        {
            ResetSession();
            return ReservationTaken(user, ex);
        }
        catch (SparkStoreConcurrencyException)
        {
            ResetSession();
            // Someone else wrote this user while we held it. Losing is a normal outcome, not a
            // fault: the caller re-reads and retries, and a single-use credential stays single-use.
            return IdentityResult.Failed(new IdentityError
            {
                Code = "ConcurrencyFailure",
                Description = "The user was modified by another request. Please try again.",
            });
        }

        if (user.Id is not null)
            handouts[user.Id] = new(newKeys, Session.GetVersion(user));
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // The document and all its reservations (email, user name, logins, passkeys) go in one save: deleted
        // separately, a user removed without its reservation blocked the address for good
        // (MCP live checks, round 1: "account could not be created" on every external sign-in). Only the user's own
        // reservations are released: another account's must never be freed by this one.
        var id = user.Id!;
        var stored = await StoredKeysAsync(user, cancellationToken);
        foreach (var key in stored.Union(LookupKeysOf(user), StringComparer.OrdinalIgnoreCase))
            Session.Release(key, id);
        foreach (var passkey in user.Passkeys)
            Session.Release(PasskeyKeyFor(passkey.CredentialId), id);

        Session.Delete(user.GetType(), id);
        await Session.SaveChangesAsync(cancellationToken);
        handouts.Remove(id);
        return IdentityResult.Success;
    }

    /// <summary>
    /// The failure for a key another user holds. A passkey's never names its holder: that would turn enrollment into a
    /// lookup oracle over a value the caller already controls.
    /// </summary>
    private static IdentityResult ReservationTaken(TUser user, SparkReservationTakenException taken)
    {
        if (taken.Key.StartsWith(EmailReservationKeyPrefix, StringComparison.OrdinalIgnoreCase))
            return IdentityResult.Failed(new IdentityError { Code = "DuplicateEmail", Description = $"Email '{user.Email}' is already taken." });
        if (taken.Key.StartsWith(UserNameReservationKeyPrefix, StringComparison.OrdinalIgnoreCase))
            return IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName", Description = $"Username '{user.UserName}' is already taken." });
        if (taken.Key.StartsWith(LoginReservationKeyPrefix, StringComparison.OrdinalIgnoreCase))
            return IdentityResult.Failed(new IdentityError { Code = "LoginAlreadyAssociated", Description = "A user with this login already exists." });
        return IdentityResult.Failed(new IdentityError { Code = "DuplicatePasskey", Description = "This passkey is already registered." });
    }

    public async Task<TUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Loaded(await Session.LoadAsync<TUser>(userId, cancellationToken));
    }

    public async Task<TUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // The reservation, then a load by id: strongly consistent on every store, and no index (D42). The check after
        // the load ignores a reservation its holder no longer matches (a rename that has not been saved yet).
        // ⚠️ A user written around this store (a raw seed, an import) holds no reservation and is not found here: such
        // code claims the keys with SparkUserReservations.ClaimAsync.
        var userId = await Session.GetReservationAsync(UserNameKeyFor(normalizedUserName), cancellationToken);
        if (string.IsNullOrEmpty(userId))
            return null;
        var user = await Session.LoadAsync<TUser>(userId, cancellationToken);
        return user is not null && string.Equals(user.NormalizedUserName, normalizedUserName, StringComparison.OrdinalIgnoreCase)
            ? Loaded(user)
            : null;
    }

    public Task<string?> GetNormalizedUserNameAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.NormalizedUserName);

    public Task<string> GetUserIdAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.Id ?? string.Empty);

    public Task<string?> GetUserNameAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.UserName);

    public Task SetNormalizedUserNameAsync(TUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    public Task SetUserNameAsync(TUser user, string? userName, CancellationToken cancellationToken)
    {
        user.UserName = userName;
        return Task.CompletedTask;
    }

    #endregion

    #region IUserPasswordStore

    public Task SetPasswordHashAsync(TUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.PasswordHash != null);

    #endregion

    #region IUserEmailStore

    public Task SetEmailAsync(TUser user, string? email, CancellationToken cancellationToken)
    {
        user.Email = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.Email);

    public Task<bool> GetEmailConfirmedAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.EmailConfirmed);

    public Task SetEmailConfirmedAsync(TUser user, bool confirmed, CancellationToken cancellationToken)
    {
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public async Task<TUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // The reservation, read now: consistent, never a stale index.
        var userId = await Session.GetReservationAsync(EmailKeyFor(normalizedEmail), cancellationToken);
        return string.IsNullOrEmpty(userId) ? null : Loaded(await Session.LoadAsync<TUser>(userId, cancellationToken));
    }

    public Task<string?> GetNormalizedEmailAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(TUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    #endregion

    #region IUserSecurityStampStore

    public Task SetSecurityStampAsync(TUser user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.SecurityStamp);

    #endregion

    #region IUserRoleStore

    // A role is a security.json group: core reads role claims as group names, matched case-insensitively
    // (SecurityFileAccessControl). UserManager hands this store the NORMALIZED name ("ADMINISTRATORS"); kept as given, it
    // became the role claim, and ClaimsPrincipal.IsInRole compares ordinally, so IsInRole("Administrators") was false for
    // the cookie and for an agent alike (both come from Identity's claims factory, which reads GetRolesAsync). So a role
    // is kept and read by the name its group is declared under; reading maps too, so a document holding another casing
    // (written before, or patched by hand) answers by the declared name, and nothing is rewritten. Membership ignores case.

    public Task AddToRoleAsync(TUser user, string roleName, CancellationToken cancellationToken)
    {
        if (!user.Roles.Contains(roleName, StringComparer.OrdinalIgnoreCase))
        {
            user.Roles.Add(DeclaredRoleName(roleName));
        }
        return Task.CompletedTask;
    }

    public Task RemoveFromRoleAsync(TUser user, string roleName, CancellationToken cancellationToken)
    {
        user.Roles.RemoveAll(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    public Task<IList<string>> GetRolesAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult<IList<string>>(user.Roles
            .Select(DeclaredRoleName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());

    public Task<bool> IsInRoleAsync(TUser user, string roleName, CancellationToken cancellationToken)
        => Task.FromResult(user.Roles.Contains(roleName, StringComparer.OrdinalIgnoreCase));

    public async Task<IList<TUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // Ignores case: the normalized name UserManager passes finds a role stored in any casing (UserRoleNameTests). A
        // rare, administrative read: it streams the users unfiltered (a collection scan, no auto index on RavenDB, #536
        // D42) and matches in memory, the same on every store.
        return Loaded(await StreamUsersAsync(u => u.Roles.Contains(roleName, StringComparer.OrdinalIgnoreCase), cancellationToken));
    }

    /// <summary>
    /// <paramref name="roleName"/> as <c>security.json</c> declares the group it names, found as core finds a role
    /// claim's group (case-insensitively, the first match); a name no group declares is returned as given.
    /// </summary>
    private string DeclaredRoleName(string roleName)
        => securityConfiguration?.GetConfiguration().Groups.Values
               .FirstOrDefault(name => string.Equals(name, roleName, StringComparison.OrdinalIgnoreCase))
           ?? roleName;

    #endregion

    #region IUserClaimStore

    public Task<IList<Claim>> GetClaimsAsync(TUser user, CancellationToken cancellationToken)
    {
        IList<Claim> claims = user.Claims
            .Select(c => new Claim(c.ClaimType, c.ClaimValue))
            .ToList();
        return Task.FromResult(claims);
    }

    public Task AddClaimsAsync(TUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
    {
        foreach (var claim in claims)
        {
            user.Claims.Add(new SparkUserClaim
            {
                ClaimType = claim.Type,
                ClaimValue = claim.Value
            });
        }
        return Task.CompletedTask;
    }

    public Task ReplaceClaimAsync(TUser user, Claim claim, Claim newClaim, CancellationToken cancellationToken)
    {
        var existing = user.Claims.FirstOrDefault(c =>
            c.ClaimType == claim.Type && c.ClaimValue == claim.Value);

        if (existing != null)
        {
            existing.ClaimType = newClaim.Type;
            existing.ClaimValue = newClaim.Value;
        }

        return Task.CompletedTask;
    }

    public Task RemoveClaimsAsync(TUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
    {
        foreach (var claim in claims)
        {
            user.Claims.RemoveAll(c => c.ClaimType == claim.Type && c.ClaimValue == claim.Value);
        }
        return Task.CompletedTask;
    }

    public async Task<IList<TUser>> GetUsersForClaimAsync(Claim claim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // A rare, administrative read, as GetUsersInRoleAsync: streamed and matched in memory.
        return Loaded(await StreamUsersAsync(u => u.Claims.Any(c => c.ClaimType == claim.Type && c.ClaimValue == claim.Value), cancellationToken));
    }

    #endregion

    #region IUserLockoutStore

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(TUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.AccessFailedCount);

    public Task<bool> GetLockoutEnabledAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.LockoutEnabled);

    public Task SetLockoutEnabledAsync(TUser user, bool enabled, CancellationToken cancellationToken)
    {
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(TUser user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount++;
        return Task.FromResult(user.AccessFailedCount);
    }

    public Task ResetAccessFailedCountAsync(TUser user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    #endregion

    #region IUserTwoFactorStore

    public Task SetTwoFactorEnabledAsync(TUser user, bool enabled, CancellationToken cancellationToken)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.TwoFactorEnabled);

    #endregion

    #region IUserAuthenticatorKeyStore

    /// <summary>Stores the key protected (<c>sdp1:</c>) — see <c>UserStore.Secrets.cs</c>.</summary>
    public Task SetAuthenticatorKeyAsync(TUser user, string key, CancellationToken cancellationToken)
    {
        user.AuthenticatorKey = Protect(key, AuthenticatorKeyPurpose);
        return Task.CompletedTask;
    }

    /// <summary>Reads legacy plaintext and protected values; an unreadable one never disables 2FA.</summary>
    public Task<string?> GetAuthenticatorKeyAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(ReadAuthenticatorKey(user));

    #endregion

    #region IUserTwoFactorRecoveryCodeStore

    public Task ReplaceCodesAsync(TUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        user.TwoFactorRecoveryCodes = [.. recoveryCodes.Select(HashRecoveryCode)];
        return Task.CompletedTask;
    }

    /// <summary>
    /// Spends a recovery code. Every stored hash is compared even after a match, and each
    /// comparison is constant-time, so neither the number of remaining codes nor which one was
    /// presented is observable through timing.
    /// </summary>
    public Task<bool> RedeemCodeAsync(TUser user, string code, CancellationToken cancellationToken)
    {
        var presented = HashRecoveryCode(code);

        string? matched = null;
        foreach (var stored in user.TwoFactorRecoveryCodes)
        {
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(presented)))
            {
                matched = stored;
            }
        }

        if (matched == null)
            return Task.FromResult(false);

        user.TwoFactorRecoveryCodes.Remove(matched);
        return Task.FromResult(true);
    }

    /// <summary>
    /// A plain SHA-256: recovery codes are generated by Identity with full entropy, so there is
    /// nothing for a dictionary to guess and no need for a salt or a work factor. Contrast a
    /// password, which is operator-chosen and needs both.
    /// </summary>
    private static string HashRecoveryCode(string code)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    public Task<int> CountCodesAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.TwoFactorRecoveryCodes.Count);

    #endregion

    #region IUserLoginStore

    public Task AddLoginAsync(TUser user, UserLoginInfo login, CancellationToken cancellationToken)
    {
        user.Logins.Add(new SparkUserLogin
        {
            LoginProvider = login.LoginProvider,
            ProviderKey = login.ProviderKey,
            ProviderDisplayName = login.ProviderDisplayName
        });
        return Task.CompletedTask;
    }

    public Task RemoveLoginAsync(TUser user, string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        user.Logins.RemoveAll(l => l.LoginProvider == loginProvider && l.ProviderKey == providerKey);
        return Task.CompletedTask;
    }

    public Task<IList<UserLoginInfo>> GetLoginsAsync(TUser user, CancellationToken cancellationToken)
    {
        IList<UserLoginInfo> logins = user.Logins
            .Select(l => new UserLoginInfo(l.LoginProvider, l.ProviderKey, l.ProviderDisplayName))
            .ToList();
        return Task.FromResult(logins);
    }

    public async Task<TUser?> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // The reservation (#536), then a load by id: no index (D42), and a relational store keeps the logins in a JSON
        // column no query can filter. The check after the load ignores a stale holder.
        // ⚠️ A user written around this store (a raw seed, an import) holds no reservation and is not found here: such
        // code claims the keys with SparkUserReservations.ClaimAsync.
        var userId = await Session.GetReservationAsync(LoginKeyFor(loginProvider, providerKey), cancellationToken);
        if (string.IsNullOrEmpty(userId))
            return null;
        var user = await Session.LoadAsync<TUser>(userId, cancellationToken);
        return user is not null && user.Logins.Any(l => l.LoginProvider == loginProvider && l.ProviderKey == providerKey)
            ? Loaded(user)
            : null;
    }

    #endregion

    #region IUserAuthenticationTokenStore

    public Task SetTokenAsync(TUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken)
    {
        var existing = user.Tokens.FirstOrDefault(t => t.LoginProvider == loginProvider && t.Name == name);
        var stored = Protect(value, TokenPurpose);
        if (existing != null)
        {
            existing.Value = stored;
        }
        else
        {
            user.Tokens.Add(new SparkUserToken
            {
                LoginProvider = loginProvider,
                Name = name,
                Value = stored
            });
        }
        return Task.CompletedTask;
    }

    public Task RemoveTokenAsync(TUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        user.Tokens.RemoveAll(t => t.LoginProvider == loginProvider && t.Name == name);
        return Task.CompletedTask;
    }

    public Task<string?> GetTokenAsync(TUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var token = user.Tokens.FirstOrDefault(t => t.LoginProvider == loginProvider && t.Name == name);
        return Task.FromResult(token is null ? null : ReadToken(user, token));
    }

    #endregion

    #region IUserPhoneNumberStore

    public Task SetPhoneNumberAsync(TUser user, string? phoneNumber, CancellationToken cancellationToken)
    {
        user.PhoneNumber = phoneNumber;
        return Task.CompletedTask;
    }

    public Task<string?> GetPhoneNumberAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.PhoneNumber);

    public Task<bool> GetPhoneNumberConfirmedAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.PhoneNumberConfirmed);

    public Task SetPhoneNumberConfirmedAsync(TUser user, bool confirmed, CancellationToken cancellationToken)
    {
        user.PhoneNumberConfirmed = confirmed;
        return Task.CompletedTask;
    }

    #endregion

    #region IQueryableUserStore

    public IQueryable<TUser> Users
    {
        get
        {
            ThrowIfDisposed();
            return Session.Query<TUser>();
        }
    }

    #endregion

    #region Reservation helpers

    private static string EmailKeyFor(string normalizedEmail) => SparkUserReservations.EmailKey(normalizedEmail);

    private static string UserNameKeyFor(string normalizedUserName) => SparkUserReservations.UserNameKey(normalizedUserName);

    private static string LoginKeyFor(string loginProvider, string providerKey) => SparkUserReservations.LoginKey(loginProvider, providerKey);

    private static HashSet<string> LookupKeysOf(SparkUser user) => SparkUserReservations.KeysOf(user);

    /// <summary>Every user matching <paramref name=match/>, streamed from the whole collection (no index).</summary>
    private async Task<IList<TUser>> StreamUsersAsync(Func<TUser, bool> match, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await foreach (var row in Session.StreamAsync(Session.Query<TUser>(), cancellationToken))
        {
            if (match(row.Document))
                ids.Add(row.Id);
        }
        if (ids.Count == 0)
            return [];
        // Tracked, as every user this store hands out, so UpdateAsync can save it: one batched load.
        var loaded = await Session.LoadAsync<TUser>(ids, cancellationToken);
        return [.. ids.Select(id => loaded.GetValueOrDefault(id)).OfType<TUser>()];
    }

    #endregion

    #region Dispose

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session?.Dispose();
    }

    #endregion
}
