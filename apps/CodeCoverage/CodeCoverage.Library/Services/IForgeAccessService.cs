using CodeCoverage.Forge;

namespace CodeCoverage.Services;

/// <summary>
/// Answers "which accounts on <see cref="Provider"/> may the signed-in viewer see and manage?",
/// for exactly one forge.
/// </summary>
/// <remarks>
/// <para>
/// <b>The forge is the authority.</b> There is no app-local ACL and no stored grant: the answer is
/// derived live from the provider on each call (behind a cache), so revoking access at the forge
/// revokes it here. An implementation must never consult a stored record as its source of truth,
/// and must never return owners belonging to a different provider — the whole point of splitting
/// this per forge is that a GitHub decision cannot be influenced by GitLab state.
/// </para>
/// <para>
/// <b>Implementations are resolved lazily, per provider.</b> A viewer with only a GitHub login must
/// never cost a GitLab API call, so callers ask only for the forges they actually need — see
/// <see cref="Forge.IForgeIntegrationResolver"/>. Rendering one forge's page must not fan out
/// to the others.
/// </para>
/// <para>
/// <b>Degradation is part of the contract, not an implementation detail.</b> When the provider is
/// unreachable or the viewer's credential is dead, an implementation returns
/// <see cref="EForgeCredentialState.Unavailable"/> / <see cref="EForgeCredentialState.ReauthRequired"/>
/// with the owner set degraded to the viewer's own account — <em>failure is not absence</em>, and a
/// degraded answer must never be cached as though it were a successful one. Every degraded return
/// MUST be logged: this is the only signal that a provider is failing, because the user-visible
/// symptom is an account list that silently loses rows.
/// </para>
/// <para>
/// <b>Failures must be cached, briefly.</b> Caching only successes is defensible with one provider
/// and dangerous with three: an outage then means every request re-attempts every unreachable
/// provider. Bitbucket's budget is roughly 1,000 requests per hour per token, low enough that
/// failures alone can exhaust it and then keep it exhausted. A short negative TTL — far shorter
/// than the success TTL — collapses the burst without pinning a stale outage.
/// </para>
/// </remarks>
public interface IForgeAccessService
{
    /// <summary>The single forge this instance answers for.</summary>
    EForgeProvider Provider { get; }

    /// <summary>
    /// The owners the current viewer may see on <see cref="Provider"/>, plus the health of their
    /// credential. Empty (not degraded) for an anonymous viewer, and degraded to the viewer's own
    /// account when the provider cannot be reached.
    /// </summary>
    Task<ForgeVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default);

    /// <summary>The owner set alone, for callers that do not care why it is what it is.</summary>
    Task<ForgeOwner[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the viewer may SEE this owner's private repositories: membership of the owner set, nothing more. ⚠️ Not a
    /// management right (dependency-updates PRD D-O12): who manages an owner is <see cref="ManagesOwnerAsync"/>.
    /// </summary>
    Task<bool> IsOwnerAllowedAsync(ForgeOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the viewer MANAGES this owner (R39, D-O12): it is their own personal account, or an organization of which the
    /// forge says they are an active owner (GitHub: <c>GET /user/memberships/orgs/{org}</c>, <c>role: admin</c>,
    /// <c>state: active</c>). Members with read or write access are not managers. Always within the owner set; fails closed;
    /// cached with the owner set's lifetime and dropped by <see cref="InvalidateAsync"/>.
    /// </summary>
    Task<bool> ManagesOwnerAsync(ForgeOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the viewer's cached owner set for this provider so the next call re-queries the forge
    /// (the manual resync). Must not disturb any other provider's cache entry.
    /// </summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The signed-in viewer's own account on <see cref="Provider"/>, as the forge returned it for the viewer's own
    /// credential, never a name the viewer can edit. Whatever the app does on the forge in the viewer's name acts as this
    /// login: the merge queue's button raises its command as it (merge-queue PRD O8, T1).
    /// </summary>
    Task<ForgeViewerAccount> GetViewerAccountAsync(CancellationToken cancellationToken = default);
}

/// <summary>The signed-in viewer's own account on one forge.</summary>
/// <param name="Login">The login the forge returned for the viewer's credential; null when there is none to give.</param>
/// <param name="Linked">The viewer's account has a sign-in with this forge at all; false for an anonymous viewer too.</param>
/// <param name="State">
/// Why a linked viewer has no login: <see cref="EForgeCredentialState.ReauthRequired"/> (sign in with the forge again) or
/// <see cref="EForgeCredentialState.Unavailable"/> (the forge could not be asked; try again).
/// </param>
public sealed record ForgeViewerAccount(string? Login, bool Linked, EForgeCredentialState State);
