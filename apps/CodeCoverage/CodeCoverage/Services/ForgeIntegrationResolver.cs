using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.AspNetCore.Identity;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace CodeCoverage.Services;

/// <inheritdoc cref="IForgeIntegrationResolver"/>
[Register(typeof(IForgeIntegrationResolver), ServiceLifetime.Scoped)]
public partial class ForgeIntegrationResolver : IForgeIntegrationResolver
{
    [Inject] private readonly IEnumerable<IForgeIntegration> integrations;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly UserManager<SparkUser> userManager;

    // Memoized for the request: the row-security hooks ask repeatedly, and this is a user load.
    private Task<IReadOnlyList<EForgeProvider>>? linkedProviders;

    public IReadOnlyList<IForgeIntegration> All => field ??= integrations
        .OrderBy(integration => integration.Provider)
        .ToArray();

    /// <remarks>
    /// Throws rather than taking the first of several, unlike the resolver this replaced. Two
    /// implementations claiming one forge is a wiring bug, and <c>FirstOrDefault</c> would make
    /// which one runs depend on DI registration order — invisible until the two disagree.
    /// <c>ActionsResolver</c> throws on the same ambiguity, after a bug that made the case for it.
    /// </remarks>
    public IForgeIntegration? For(EForgeProvider provider)
    {
        IForgeIntegration? found = null;
        foreach (var integration in integrations)
        {
            if (integration.Provider != provider) continue;
            if (found is not null)
                throw new InvalidOperationException(
                    $"More than one {nameof(IForgeIntegration)} is registered for {provider}: " +
                    $"{found.GetType().Name} and {integration.GetType().Name}. Exactly one implementation may claim a forge.");
            found = integration;
        }

        return found;
    }

    public IForgeIntegration For(Repository repository)
    {
        var provider = ProviderOf(repository);
        return For(provider)
            ?? throw new InvalidOperationException(
                $"No {nameof(IForgeIntegration)} is registered for {provider}, required by repository '{repository.Id}'. " +
                "A repository whose forge has no implementation cannot be serviced.");
    }

    /// <summary>
    /// The forge a repository is hosted on, read from the document.
    /// </summary>
    /// <remarks>
    /// Until M6 this returned <see cref="EForgeProvider.GitHub"/> unconditionally, as an explicit
    /// placeholder: every stored repository was a GitHub one, and saying so in a single named
    /// method was better than letting a <c>FirstOrDefault</c> arrive at GitHub by accident because
    /// it happened to be the only registration. M6 gave <see cref="Repository.Provider"/> a real
    /// value on every document, so the placeholder is gone.
    /// </remarks>
    private static EForgeProvider ProviderOf(Repository repository) => repository.Provider;

    public Task<IReadOnlyList<EForgeProvider>> GetLinkedProvidersAsync(CancellationToken cancellationToken = default)
        => linkedProviders ??= QueryLinkedProvidersAsync();

    /// <summary>
    /// Derived from the viewer's own external logins, never from the set of registered forges.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the type. Iterating every known forge would mean a viewer who has
    /// only ever signed in with GitHub pays for a GitLab and a Bitbucket API call on every request
    /// that asks "what may you see?" — and those calls would fail, which under a negative cache is
    /// merely wasteful and without one is a retry storm. A forge the viewer has no identity on can
    /// have nothing to show them, so asking is pointless as well as expensive.
    /// </remarks>
    private async Task<IReadOnlyList<EForgeProvider>> QueryLinkedProvidersAsync()
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
            return [];

        var user = await userManager.GetUserAsync(principal);
        if (user?.Logins is null)
            return [];

        // Matched against the canonical spelling rather than the ASP.NET scheme name, so a scheme
        // registered as "GitHub" and one registered as "github" resolve identically. A login whose
        // provider we do not recognise is skipped, not an error: an app may register a non-forge
        // external provider (Google, Microsoft) purely for sign-in.
        return user.Logins
            .Select(login => ForgeProviders.TryParse(login.LoginProvider, out var provider)
                ? provider
                : (EForgeProvider?)null)
            .OfType<EForgeProvider>()
            .Distinct()
            .Where(provider => For(provider) is not null)
            .OrderBy(provider => provider)
            .ToArray();
    }
}
