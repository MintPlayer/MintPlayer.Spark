using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// A provider preset's declaration of its <see cref="SparkExternalProviderPolicy"/>, registered in DI by
/// the preset (<c>AddGitHub</c>, <c>AddSparkGoogle</c>, …) under its scheme name.
/// </summary>
/// <remarks>
/// A DI registration rather than a write into <see cref="SparkAuthenticationOptions"/>, because presets
/// run inside <c>configureProviders</c>, after the options object is already built and published.
/// <see cref="SparkAuthenticationOptions.ExternalProviders"/> still wins, so an application can override
/// what a preset declares.
/// </remarks>
public sealed record SparkExternalProviderRegistration(string Scheme, SparkExternalProviderPolicy Policy);

internal static class SparkExternalProviderPolicies
{
    /// <summary>Options entry, else the preset's registration, else <see cref="SparkExternalProviderPolicy.Default"/>.</summary>
    internal static SparkExternalProviderPolicy For(IServiceProvider services, string scheme)
    {
        var options = services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value;
        if (options is not null && options.ExternalProviders.TryGetValue(scheme, out var configured))
            return configured;

        var registered = services.GetServices<SparkExternalProviderRegistration>()
            .LastOrDefault(r => string.Equals(r.Scheme, scheme, StringComparison.OrdinalIgnoreCase));

        return registered?.Policy ?? SparkExternalProviderPolicy.Default;
    }
}
