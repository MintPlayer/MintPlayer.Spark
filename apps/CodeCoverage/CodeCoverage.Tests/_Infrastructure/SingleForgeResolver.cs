using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.Tests;

/// <summary>
/// An <see cref="IForgeIntegrationResolver"/> over exactly one integration.
/// </summary>
/// <remarks>
/// <para>
/// The production resolver depends on <c>IHttpContextAccessor</c> and <c>UserManager</c> to answer
/// "which forges is this viewer signed in to?", which a fixture testing something else should not
/// have to stand up. This answers every lookup with the one integration it was given.
/// </para>
/// <para>
/// Use it to keep the <em>real</em> implementation in the loop — wrapping
/// <c>GitHubForgeIntegration</c> preserves the behaviour under test, where substituting the whole
/// integration would quietly replace it. Selection itself is covered against the production
/// resolver.
/// </para>
/// </remarks>
public sealed class SingleForgeResolver(IForgeIntegration forge) : IForgeIntegrationResolver
{
    public IForgeIntegration? For(EForgeProvider provider) => provider == forge.Provider ? forge : null;

    public IForgeIntegration For(Repository repository) => forge;

    public IReadOnlyList<IForgeIntegration> All => [forge];

    public Task<IReadOnlyList<EForgeProvider>> GetLinkedProvidersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<EForgeProvider>>([forge.Provider]);
}
