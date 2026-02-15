using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Authorization.Identity;

/// <summary>
/// RavenDB-backed role store for Spark authentication.
/// Uses deterministic document IDs based on the role name.
/// </summary>
public class RoleStore : IRoleStore<SparkRole>, IRoleClaimStore<SparkRole>, IQueryableRoleStore<SparkRole>
{
    private readonly IDocumentStore documentStore;
    private IAsyncDocumentSession? session;
    private bool disposed;

    public RoleStore(IDocumentStore documentStore)
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

    #region IRoleStore

    public async Task<IdentityResult> CreateAsync(SparkRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (role.Name != null)
        {
            role.Id = GetRoleDocumentId(role.Name);
        }

        await Session.StoreAsync(role, cancellationToken);
        await Session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(SparkRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await Session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(SparkRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        Session.Delete(role);
        await Session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public Task<string> GetRoleIdAsync(SparkRole role, CancellationToken cancellationToken)
        => Task.FromResult(role.Id ?? string.Empty);

    public Task<string?> GetRoleNameAsync(SparkRole role, CancellationToken cancellationToken)
        => Task.FromResult(role.Name);

    public Task SetRoleNameAsync(SparkRole role, string? roleName, CancellationToken cancellationToken)
    {
        role.Name = roleName;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedRoleNameAsync(SparkRole role, CancellationToken cancellationToken)
        => Task.FromResult(role.NormalizedName);

    public Task SetNormalizedRoleNameAsync(SparkRole role, string? normalizedName, CancellationToken cancellationToken)
    {
        role.NormalizedName = normalizedName;
        return Task.CompletedTask;
    }

    public async Task<SparkRole?> FindByIdAsync(string roleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        return await Session.LoadAsync<SparkRole>(roleId, cancellationToken);
    }

    public async Task<SparkRole?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var roleId = GetRoleDocumentId(normalizedRoleName);
        return await Session.LoadAsync<SparkRole>(roleId, cancellationToken);
    }

    #endregion

    #region IRoleClaimStore

    public Task<IList<Claim>> GetClaimsAsync(SparkRole role, CancellationToken cancellationToken = default)
    {
        IList<Claim> claims = role.Claims
            .Select(c => new Claim(c.ClaimType, c.ClaimValue))
            .ToList();
        return Task.FromResult(claims);
    }

    public Task AddClaimAsync(SparkRole role, Claim claim, CancellationToken cancellationToken = default)
    {
        role.Claims.Add(new SparkRoleClaim
        {
            ClaimType = claim.Type,
            ClaimValue = claim.Value
        });
        return Task.CompletedTask;
    }

    public Task RemoveClaimAsync(SparkRole role, Claim claim, CancellationToken cancellationToken = default)
    {
        role.Claims.RemoveAll(c => c.ClaimType == claim.Type && c.ClaimValue == claim.Value);
        return Task.CompletedTask;
    }

    #endregion

    #region IQueryableRoleStore

    public IQueryable<SparkRole> Roles
    {
        get
        {
            ThrowIfDisposed();
            return Session.Query<SparkRole>();
        }
    }

    #endregion

    #region Helpers

    private string GetRoleDocumentId(string roleName)
    {
        var collectionName = documentStore.Conventions.GetCollectionName(typeof(SparkRole));
        var prefix = documentStore.Conventions.TransformTypeCollectionNameToDocumentIdPrefix(collectionName);
        var separator = documentStore.Conventions.IdentityPartsSeparator;
        return $"{prefix}{separator}{roleName.ToLowerInvariant()}";
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
