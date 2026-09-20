using CodeCoverage.Forge;

namespace CodeCoverage.Services;

/// <summary>
/// The fan-out half of <see cref="IForgeIntegrationResolver"/>: operations that ask <em>every</em>
/// forge the viewer has an identity on, rather than the one that owns a repository.
/// </summary>
/// <remarks>
/// Extensions rather than interface members so that an implementation cannot get the iteration
/// subtly wrong — in particular, so no implementation can iterate every <em>registered</em> forge
/// when it should iterate the viewer's <em>linked</em> ones. That distinction is what keeps a
/// GitHub-only viewer from paying for a GitLab and a Bitbucket call on every request.
/// </remarks>
public static class ForgeFanOut
{
    /// <summary>
    /// Every owner the viewer may see, across all the forges they are signed in to.
    /// </summary>
    public static async Task<ForgeOwner[]> GetAllowedOwnersAsync(
        this IForgeIntegrationResolver forges, CancellationToken cancellationToken = default)
    {
        var owners = new List<ForgeOwner>();
        foreach (var provider in await forges.GetLinkedProvidersAsync(cancellationToken))
        {
            if (forges.For(provider) is not { } forge) continue;
            owners.AddRange(await forge.GetAllowedOwnersAsync(cancellationToken));
        }

        return [.. owners];
    }

    /// <summary>
    /// The same set flattened to bare login strings.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Temporary, and it throws away the safety property <see cref="ForgeOwner"/> exists to
    /// provide.</b> Stored documents hold unqualified logins until M6 re-keys them (D5/D7), so every
    /// comparison site still matches on a bare string. Which means a GitLab group named
    /// <c>microsoft</c> and a GitHub organisation named <c>microsoft</c> would be <em>the same
    /// string</em> here — indistinguishable, and therefore a silent authorization widening.
    /// <para>
    /// That is safe only for exactly as long as GitHub is the only registered forge. This lives in
    /// one place so M6f has one call to delete rather than four, and so the danger is stated once
    /// rather than re-derived at each site.
    /// </para>
    /// </remarks>
    public static async Task<string[]> GetAllowedOwnerLoginsAsync(
        this IForgeIntegrationResolver forges, CancellationToken cancellationToken = default)
        => [.. (await forges.GetAllowedOwnersAsync(cancellationToken))
            .Select(owner => owner.Login)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Whether the viewer may manage the owner of this repository, asked of the forge that owns it.
    /// </summary>
    public static Task<bool> CanManageAsync(
        this IForgeIntegrationResolver forges, Entities.Repository repository, CancellationToken cancellationToken = default)
    {
        var forge = forges.For(repository);
        return forge.IsOwnerAllowedAsync(new ForgeOwner(forge.Provider, repository.OwnerLogin), cancellationToken);
    }

    /// <summary>
    /// Drops the viewer's cached owner set on every forge they are signed in to — the manual resync.
    /// </summary>
    /// <remarks>
    /// Deliberately all of them, not just the one a page happens to be showing: the resync exists
    /// because the viewer believes what we display is stale, and refreshing one forge while leaving
    /// another cached would make the button appear not to work.
    /// </remarks>
    public static async Task InvalidateAllAsync(
        this IForgeIntegrationResolver forges, CancellationToken cancellationToken = default)
    {
        foreach (var provider in await forges.GetLinkedProvidersAsync(cancellationToken))
        {
            if (forges.For(provider) is not { } forge) continue;
            await forge.InvalidateAsync(cancellationToken);
        }
    }
}
