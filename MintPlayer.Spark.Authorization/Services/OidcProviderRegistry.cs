using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Stores all registered OIDC providers. Registered as a singleton.
/// Receives all <see cref="IConfigureOidcProvider"/> instances registered in DI
/// and applies them on construction.
/// </summary>
internal class OidcProviderRegistry
{
    private readonly List<OidcProviderRegistration> _providers = [];

    public OidcProviderRegistry(IEnumerable<IConfigureOidcProvider> configurators)
    {
        foreach (var configurator in configurators)
        {
            configurator.Configure(this);
        }
    }

    public void Add(OidcProviderRegistration registration) => _providers.Add(registration);

    public IReadOnlyList<OidcProviderRegistration> GetAll() => _providers;

    public OidcProviderRegistration? GetByScheme(string scheme) =>
        _providers.FirstOrDefault(p => string.Equals(p.Scheme, scheme, StringComparison.OrdinalIgnoreCase));
}
