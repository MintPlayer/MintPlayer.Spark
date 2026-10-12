using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Session;
using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// RavenDB-backed user store implementing all ASP.NET Core Identity store interfaces.
/// Adapted from RavenDB.Identity (MIT licensed) for MintPlayer.Spark's infrastructure.
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
    private const string EmailReservationKeyPrefix = "emails/";

    [Inject] private readonly IDocumentStore documentStore;
    // All [Inject] fields live in this file: the generator emits one constructor per partial
    // declaration, so a field injected from UserStore.Secrets.cs produced a second, ambiguous one.
    [Inject] private readonly Microsoft.AspNetCore.DataProtection.IDataProtectionProvider? dataProtectionProvider;
    [Inject] private readonly Microsoft.Extensions.Logging.ILogger<UserStore<TUser>>? logger;
    [Inject] private readonly TimeProvider? timeProvider;
    // The groups security.json declares, whose names roles keep (see IUserRoleStore below). Absent without core's loader
    // (a bare AddSparkAuthentication, the store's unit tests): a role then keeps the name the store was handed.
    [Inject] private readonly ISecurityConfigurationLoader? securityConfiguration;
    private IAsyncDocumentSession? session;
    private bool disposed;

    private IAsyncDocumentSession Session
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (session != null)
                return session;

            session = documentStore.OpenAsyncSession();

            // Identity's stores are read-modify-write over a whole user document: load the user,
            // mutate a field, save. Without optimistic concurrency the last save wins silently,
            // which loses updates that matter. The sharpest case is a recovery code — two
            // concurrent redemptions each load the user, each remove the same code from their own
            // copy, and both succeed, so a single-use two-factor bypass is spendable twice. The
            // failed-access counter behind lockout has the same shape.
            //
            // Identity models this outcome already: UpdateAsync answers ConcurrencyFailure, which
            // UserManager surfaces as a failed IdentityResult rather than an exception.
            session.Advanced.UseOptimisticConcurrency = true;
            return session;
        }
    }

    private string DatabaseName => documentStore.Database;

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
        // from the start. It used to hold "" until the save, and a save that failed left that
        // placeholder blocking the address forever.
        await Session.StoreAsync(user, cancellationToken);

        var normalizedEmail = user.Email is null ? null : user.NormalizedEmail ?? user.Email.ToUpperInvariant();
        if (normalizedEmail != null)
        {
            // Reserve email via compare/exchange (cluster-safe uniqueness)
            if (!await CreateEmailReservationAsync(normalizedEmail, user.Id!, cancellationToken))
            {
                Session.Advanced.Evict(user);
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "DuplicateEmail",
                    Description = $"Email '{user.Email}' is already taken."
                });
            }
        }

        try
        {
            await Session.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (normalizedEmail != null)
                await DeleteEmailReservationAsync(normalizedEmail, user.Id!, CancellationToken.None);
            throw;
        }

        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // Detect email changes
        string? oldEmail = null;
        string? newEmail = null;
        var changes = Session.Advanced.WhatChanged();
        if (user.Id != null && changes.TryGetValue(user.Id, out var documentChanges))
        {
            var emailChange = documentChanges.FirstOrDefault(c =>
                string.Equals(c.FieldName, nameof(SparkUser.NormalizedEmail), StringComparison.OrdinalIgnoreCase));

            if (emailChange != null)
            {
                oldEmail = emailChange.FieldOldValue?.ToString();
                newEmail = user.NormalizedEmail;
                // A change of case only keeps the same reservation key: nothing to move.
                if (oldEmail != null && newEmail != null && CompareExchangeKeyFor(oldEmail) == CompareExchangeKeyFor(newEmail))
                    oldEmail = newEmail = null;
            }
        }

        // The new address is reserved first and the old one released only after the user document
        // is saved: a refused save (ConcurrencyFailure) used to leave the old address unreserved and
        // the new one held by a user who never got it, blocking it for everyone.
        if (newEmail != null && !await CreateEmailReservationAsync(newEmail, user.Id!, cancellationToken))
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = "DuplicateEmail",
                Description = $"Email '{user.Email}' is already taken."
            });
        }

        try
        {
            await Session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (newEmail != null)
                await DeleteEmailReservationAsync(newEmail, user.Id!, CancellationToken.None);

            if (ex is not Raven.Client.Exceptions.ConcurrencyException)
                throw;

            // Someone else wrote this user while we held it. Losing is a normal outcome, not a
            // fault: the caller re-reads and retries, and a single-use credential stays single-use.
            return IdentityResult.Failed(new IdentityError
            {
                Code = "ConcurrencyFailure",
                Description = "The user was modified by another request. Please try again.",
            });
        }

        if (oldEmail != null)
            await DeleteEmailReservationAsync(oldEmail, user.Id!, cancellationToken);

        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // Passkeys hold reservations of their own. Skipping this would burn every one of the user's
        // credential ids permanently and leave them pointing at a deleted document.
        await DeleteAllPasskeyReservationsAsync(user, cancellationToken);

        // The document and its email reservation go in one cluster-wide transaction: deleted
        // separately, a user removed without its reservation blocked the address for good
        // (MCP live checks, round 1: "account could not be created" on every external sign-in).
        using (var clusterSession = documentStore.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions
        {
            Database = DatabaseName,
            TransactionMode = TransactionMode.ClusterWide,
            // Users are written by ordinary sessions, so they carry no atomic guard to check.
            DisableAtomicDocumentWritesInClusterWideTransaction = true,
        }))
        {
            clusterSession.Delete(user.Id!);
            if (user.NormalizedEmail != null)
            {
                var key = CompareExchangeKeyFor(user.NormalizedEmail);
                var reservation = await clusterSession.Advanced.ClusterTransaction
                    .GetCompareExchangeValueAsync<string>(key, cancellationToken);
                // Only the user's own reservation: another account's must never be released by this one.
                if (reservation != null && (reservation.Value == user.Id || string.IsNullOrEmpty(reservation.Value)))
                    clusterSession.Advanced.ClusterTransaction.DeleteCompareExchangeValue(key, reservation.Index);
            }
            await clusterSession.SaveChangesAsync(cancellationToken);
        }

        if (Session.Advanced.IsLoaded(user.Id!))
            Session.Advanced.Evict(user);
        return IdentityResult.Success;
    }

    public async Task<TUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return await Session.LoadAsync<TUser>(userId, cancellationToken);
    }

    public async Task<TUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        return await Session.Query<TUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);
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

        // Use compare/exchange for consistent read (no stale index issues)
        var key = CompareExchangeKeyFor(normalizedEmail);
        var operations = documentStore.Operations.ForDatabase(DatabaseName);
        var result = await operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);

        if (result?.Value == null || string.IsNullOrEmpty(result.Value))
        {
            return null;
        }

        return await Session.LoadAsync<TUser>(result.Value, cancellationToken);
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

        // Ignores case: RavenDB matches an equality on an auto-index field case-insensitively, so the normalized name
        // UserManager passes finds a role stored in any casing (UserRoleNameTests).
        return await Session.Query<TUser>()
            .Where(u => u.Roles.Contains(roleName))
            .ToListAsync(cancellationToken);
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

        // RavenDB can't auto-index .Any() with multiple fields. Filter by one field in
        // the query, then verify the second in memory — same pattern as FindByLoginAsync.
        var candidates = await Session.Query<TUser>()
            .Where(u => u.Claims.Any(c => c.ClaimValue == claim.Value))
            .ToListAsync(cancellationToken);

        return candidates
            .Where(u => u.Claims.Any(c => c.ClaimType == claim.Type && c.ClaimValue == claim.Value))
            .ToList();
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

        // RavenDB can't auto-index .Any() with multiple fields.
        // Filter by one field in the query, then verify the second in memory.
        var candidates = await Session.Query<TUser>()
            .Where(u => u.Logins.Any(l => l.ProviderKey == providerKey))
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(u =>
            u.Logins.Any(l => l.LoginProvider == loginProvider && l.ProviderKey == providerKey));
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

    #region Compare/Exchange helpers

    private static string CompareExchangeKeyFor(string normalizedEmail)
        => EmailReservationKeyPrefix + normalizedEmail.ToLowerInvariant();

    private async Task<bool> CreateEmailReservationAsync(string normalizedEmail, string userId, CancellationToken cancellationToken)
    {
        var key = CompareExchangeKeyFor(normalizedEmail);
        var operations = documentStore.Operations.ForDatabase(DatabaseName);
        var result = await operations.SendAsync(
            new PutCompareExchangeValueOperation<string>(key, userId, 0),
            token: cancellationToken);
        if (result.Successful)
            return true;

        // Self-healing: a reservation whose user document no longer exists is stale (the user was
        // removed without releasing it) and is taken over — atomically, against the index just read,
        // so two claimants cannot both win. Never while the user it names exists. An empty value is a
        // creation in flight under the old protocol and is left alone.
        var existing = await operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key), token: cancellationToken);
        if (existing is null)
            return (await operations.SendAsync(
                new PutCompareExchangeValueOperation<string>(key, userId, 0), token: cancellationToken)).Successful;
        if (existing.Value == userId)
            return true;
        if (string.IsNullOrEmpty(existing.Value))
            return false;

        using (var probe = documentStore.OpenAsyncSession(DatabaseName))
        {
            if (await probe.Advanced.ExistsAsync(existing.Value, cancellationToken))
                return false;
        }

        logger?.LogWarning("Taking over a stale email reservation whose user document no longer exists.");
        var takeover = await operations.SendAsync(
            new PutCompareExchangeValueOperation<string>(key, userId, existing.Index),
            token: cancellationToken);
        return takeover.Successful;
    }

    /// <summary>Releases <paramref name="normalizedEmail"/>'s reservation, but only while it is <paramref name="userId"/>'s.</summary>
    private async Task DeleteEmailReservationAsync(string normalizedEmail, string userId, CancellationToken cancellationToken)
    {
        var key = CompareExchangeKeyFor(normalizedEmail);
        var operations = documentStore.Operations.ForDatabase(DatabaseName);
        var existing = await operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key),
            token: cancellationToken);

        if (existing != null && existing.Value == userId)
        {
            await operations.SendAsync(
                new DeleteCompareExchangeValueOperation<string>(key, existing.Index),
                token: cancellationToken);
        }
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
