using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// RavenDB-backed user store implementing all ASP.NET Core Identity store interfaces.
/// Adapted from RavenDB.Identity (MIT licensed) for MintPlayer.Spark's infrastructure.
/// </summary>
public class UserStore<TUser> :
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
    IQueryableUserStore<TUser>
    where TUser : SparkUser
{
    private const string EmailReservationKeyPrefix = "emails/";

    private readonly IDocumentStore documentStore;
    private IAsyncDocumentSession? session;
    private bool disposed;

    public UserStore(IDocumentStore documentStore)
    {
        this.documentStore = documentStore;
    }

    private IAsyncDocumentSession Session
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return session ??= documentStore.OpenAsyncSession();
        }
    }

    private string DatabaseName => documentStore.Database;

    #region IUserStore

    public async Task<IdentityResult> CreateAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (user.Email != null)
        {
            // Reserve email via compare/exchange (cluster-safe uniqueness)
            var reserveResult = await CreateEmailReservationAsync(user.NormalizedEmail ?? user.Email.ToUpperInvariant(), string.Empty, cancellationToken);
            if (!reserveResult)
            {
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "DuplicateEmail",
                    Description = $"Email '{user.Email}' is already taken."
                });
            }
        }

        await Session.StoreAsync(user, cancellationToken);
        await Session.SaveChangesAsync(cancellationToken);

        // Update email reservation with the actual user ID
        if (user.Email != null && user.Id != null)
        {
            await UpdateEmailReservationAsync(user.NormalizedEmail ?? user.Email.ToUpperInvariant(), user.Id, cancellationToken);
        }

        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        // Detect email changes
        var changes = Session.Advanced.WhatChanged();
        if (user.Id != null && changes.TryGetValue(user.Id, out var documentChanges))
        {
            var emailChanged = documentChanges.Any(c =>
                string.Equals(c.FieldName, nameof(SparkUser.NormalizedEmail), StringComparison.OrdinalIgnoreCase));

            if (emailChanged)
            {
                var oldEmail = documentChanges
                    .Where(c => string.Equals(c.FieldName, nameof(SparkUser.NormalizedEmail), StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.FieldOldValue?.ToString())
                    .FirstOrDefault();

                if (oldEmail != null)
                {
                    await DeleteEmailReservationAsync(oldEmail, cancellationToken);
                }

                if (user.NormalizedEmail != null)
                {
                    var reserved = await CreateEmailReservationAsync(user.NormalizedEmail, user.Id, cancellationToken);
                    if (!reserved)
                    {
                        return IdentityResult.Failed(new IdentityError
                        {
                            Code = "DuplicateEmail",
                            Description = $"Email '{user.Email}' is already taken."
                        });
                    }
                }
            }
        }

        await Session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(TUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (user.NormalizedEmail != null)
        {
            await DeleteEmailReservationAsync(user.NormalizedEmail, cancellationToken);
        }

        Session.Delete(user);
        await Session.SaveChangesAsync(cancellationToken);
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

    public Task AddToRoleAsync(TUser user, string roleName, CancellationToken cancellationToken)
    {
        if (!user.Roles.Contains(roleName, StringComparer.OrdinalIgnoreCase))
        {
            user.Roles.Add(roleName);
        }
        return Task.CompletedTask;
    }

    public Task RemoveFromRoleAsync(TUser user, string roleName, CancellationToken cancellationToken)
    {
        user.Roles.RemoveAll(r => string.Equals(r, roleName, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    public Task<IList<string>> GetRolesAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult<IList<string>>(user.Roles);

    public Task<bool> IsInRoleAsync(TUser user, string roleName, CancellationToken cancellationToken)
        => Task.FromResult(user.Roles.Contains(roleName, StringComparer.OrdinalIgnoreCase));

    public async Task<IList<TUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        return await Session.Query<TUser>()
            .Where(u => u.Roles.Contains(roleName))
            .ToListAsync(cancellationToken);
    }

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

        return await Session.Query<TUser>()
            .Where(u => u.Claims.Any(c => c.ClaimType == claim.Type && c.ClaimValue == claim.Value))
            .ToListAsync(cancellationToken);
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

    public Task SetAuthenticatorKeyAsync(TUser user, string key, CancellationToken cancellationToken)
    {
        user.AuthenticatorKey = key;
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(TUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.AuthenticatorKey);

    #endregion

    #region IUserTwoFactorRecoveryCodeStore

    public Task ReplaceCodesAsync(TUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        user.TwoFactorRecoveryCodes = recoveryCodes.ToList();
        return Task.CompletedTask;
    }

    public Task<bool> RedeemCodeAsync(TUser user, string code, CancellationToken cancellationToken)
    {
        var redeemed = user.TwoFactorRecoveryCodes.Remove(code);
        return Task.FromResult(redeemed);
    }

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

        return await Session.Query<TUser>()
            .FirstOrDefaultAsync(u => u.Logins.Any(l =>
                l.LoginProvider == loginProvider && l.ProviderKey == providerKey), cancellationToken);
    }

    #endregion

    #region IUserAuthenticationTokenStore

    public Task SetTokenAsync(TUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken)
    {
        var existing = user.Tokens.FirstOrDefault(t => t.LoginProvider == loginProvider && t.Name == name);
        if (existing != null)
        {
            existing.Value = value;
        }
        else
        {
            user.Tokens.Add(new SparkUserToken
            {
                LoginProvider = loginProvider,
                Name = name,
                Value = value
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
        return Task.FromResult(token?.Value);
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
        return result.Successful;
    }

    private async Task UpdateEmailReservationAsync(string normalizedEmail, string userId, CancellationToken cancellationToken)
    {
        var key = CompareExchangeKeyFor(normalizedEmail);
        var operations = documentStore.Operations.ForDatabase(DatabaseName);
        var existing = await operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key),
            token: cancellationToken);

        if (existing != null)
        {
            await operations.SendAsync(
                new PutCompareExchangeValueOperation<string>(key, userId, existing.Index),
                token: cancellationToken);
        }
    }

    private async Task DeleteEmailReservationAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        var key = CompareExchangeKeyFor(normalizedEmail);
        var operations = documentStore.Operations.ForDatabase(DatabaseName);
        var existing = await operations.SendAsync(
            new GetCompareExchangeValueOperation<string>(key),
            token: cancellationToken);

        if (existing != null)
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
