using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.Authorization.Services;

/// <summary>
/// Marker interface for OIDC provider registration entries.
/// Each call to AddOidcLogin() registers one of these in DI.
/// </summary>
internal interface IConfigureOidcProvider
{
    void Configure(OidcProviderRegistry registry);
}

/// <summary>
/// Registers a single OIDC provider into the registry during DI resolution.
/// </summary>
internal class ConfigureOidcProvider : IConfigureOidcProvider
{
    private readonly string _scheme;
    private readonly SparkOidcLoginOptions _options;

    public ConfigureOidcProvider(string scheme, SparkOidcLoginOptions options)
    {
        _scheme = scheme;
        _options = options;
    }

    public void Configure(OidcProviderRegistry registry)
    {
        if (registry.GetByScheme(_scheme) != null) return;

        registry.Add(new OidcProviderRegistration
        {
            Scheme = _scheme,
            Options = _options,
        });
    }
}
