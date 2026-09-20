using CodeCoverage.Forge;
using Microsoft.AspNetCore.Identity;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace CodeCoverage.Services;

/// <inheritdoc cref="IForgeAccessResolver"/>
[Register(typeof(IForgeAccessResolver), ServiceLifetime.Scoped)]
public partial class ForgeAccessResolver : IForgeAccessResolver
{
    [Inject] private readonly IEnumerable<IForgeAccessService> services;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly UserManager<SparkUser> userManager;

    // Memoized for the request: the row-security hooks ask repeatedly, and this is a user load.
    private Task<IReadOnlyList<EForgeProvider>>? linkedProviders;

    public IForgeAccessService? For(EForgeProvider provider)
        => services.FirstOrDefault(service => service.Provider == provider);

    public Task<IReadOnlyList<EForgeProvider>> GetLinkedProvidersAsync(CancellationToken cancellationToken = default)
        => linkedProviders ??= QueryLinkedProvidersAsync();

    /// <summary>
    /// Derived from the viewer's own external logins, never from the set of registered providers.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the type. Iterating every known forge would mean a viewer who has
    /// only ever signed in with GitHub pays for a GitLab and a Bitbucket API call on every request
    /// that asks "what may you see?" — and those calls would fail, which under a negative cache is
    /// merely wasteful and without one is a retry storm. A provider the viewer has no identity on
    /// can have nothing to show them, so asking is pointless as well as expensive.
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
        // provider we do not recognise is skipped, not an error: an app may register a
        // non-forge external provider (Google, Microsoft) purely for sign-in.
        var providers = user.Logins
            .Select(login => ForgeProviders.TryParse(login.LoginProvider, out var provider)
                ? provider
                : (EForgeProvider?)null)
            .OfType<EForgeProvider>()
            .Distinct()
            .Where(provider => For(provider) is not null)
            .OrderBy(provider => provider)
            .ToArray();

        return providers;
    }
}
