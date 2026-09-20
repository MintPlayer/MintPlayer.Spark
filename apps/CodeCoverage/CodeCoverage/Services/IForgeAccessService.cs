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
/// never cost a GitLab API call, so callers ask only for the providers they actually need — see
/// <see cref="IForgeAccessResolver"/>. Rendering one provider's page must not fan out to the others.
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
    /// Whether the viewer may manage this owner. ⚠️ Today this is set membership and nothing more —
    /// no forge role, permission level or admin flag is consulted, so anyone who can see an
    /// account's grant can manage it. Generalising across forges must not silently widen that.
    /// </summary>
    Task<bool> IsOwnerAllowedAsync(ForgeOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the viewer's cached owner set for this provider so the next call re-queries the forge
    /// (the manual resync). Must not disturb any other provider's cache entry.
    /// </summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Hands out the <see cref="IForgeAccessService"/> for a given forge, and says which forges the
/// current viewer actually has an identity on.
/// </summary>
/// <remarks>
/// This exists so that "which providers do we consult?" is answered once, from the viewer's linked
/// external logins, rather than by each caller looping over every known forge. That is what keeps a
/// GitHub-only viewer from paying for GitLab and Bitbucket calls on every request.
/// </remarks>
public interface IForgeAccessResolver
{
    /// <summary>The service for one forge, or <c>null</c> when that forge has no implementation registered.</summary>
    IForgeAccessService? For(EForgeProvider provider);

    /// <summary>
    /// The forges the current viewer holds a linked identity on, in a stable order. Empty for an
    /// anonymous viewer. Callers that genuinely need every provider iterate this — never the full
    /// <see cref="EForgeProvider"/> enum.
    /// </summary>
    Task<IReadOnlyList<EForgeProvider>> GetLinkedProvidersAsync(CancellationToken cancellationToken = default);
}
