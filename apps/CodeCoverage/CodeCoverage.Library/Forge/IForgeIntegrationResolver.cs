using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// Picks the right <see cref="IForgeIntegration"/>, so that no caller ever has to.
/// </summary>
/// <remarks>
/// <para>
/// This type exists to make one property true: <b>a call site never names a forge.</b> Without it,
/// consumers inject the singular interface and the last registration silently wins — which is
/// exactly the defect this replaced (PRD §5.7a): nine call sites took an <c>IForgeClient</c> that
/// looked neutral and would have been redirected wholesale by adding a second implementation.
/// </para>
/// <para>
/// <b>Selection is data, not control flow.</b> <see cref="For(Repository)"/> reads the forge off the
/// document, so the answer lives in the database rather than in a branch someone has to remember to
/// extend. This matters beyond tidiness for D6f: an unauthenticated fork upload arrives with no
/// credential at all, so the repository document is the <em>only</em> thing that can decide which
/// integration handles it.
/// </para>
/// </remarks>
public interface IForgeIntegrationResolver
{
    /// <summary>
    /// The integration for one forge.
    /// </summary>
    /// <returns>Null when no implementation is registered for <paramref name="provider"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// More than one implementation claims <paramref name="provider"/>. That is a wiring bug, and
    /// silently taking the first would make which one runs depend on registration order.
    /// </exception>
    IForgeIntegration? For(EForgeProvider provider);

    /// <summary>
    /// The integration that owns this repository, chosen from the document.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No implementation is registered for the repository's forge — a repository we can no longer
    /// service at all. Loud on purpose: the alternative is a silent no-op that reads as "this
    /// commit has no coverage".
    /// </exception>
    IForgeIntegration For(Repository repository);

    /// <summary>
    /// Every registered integration, for fan-out reads. Ordered by <see cref="EForgeProvider"/> so
    /// output does not depend on DI registration order.
    /// </summary>
    IReadOnlyList<IForgeIntegration> All { get; }

    /// <summary>
    /// The forges the current viewer holds a linked identity on, in a stable order. Empty for an
    /// anonymous viewer.
    /// </summary>
    /// <remarks>
    /// Callers that need "every forge that matters to this person" iterate <em>this</em>, never
    /// <see cref="All"/> and never the <see cref="EForgeProvider"/> enum. It is what keeps a
    /// GitHub-only viewer from paying for a GitLab and a Bitbucket call on every request.
    /// </remarks>
    Task<IReadOnlyList<EForgeProvider>> GetLinkedProvidersAsync(CancellationToken cancellationToken = default);
}
