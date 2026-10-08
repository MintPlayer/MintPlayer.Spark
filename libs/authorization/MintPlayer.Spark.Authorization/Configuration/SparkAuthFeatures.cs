using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// The optional sign-in features that decide whether an endpoint exists, read the one way each
/// endpoint's <c>IsEnabled</c> reads them (endpoints generator completion D5). Same rule as
/// <see cref="LocalCredentialMode"/>: the <b>root</b> provider, so <see cref="IOptions{TOptions}"/> only,
/// and the option's own default when nothing is registered.
/// </summary>
internal static class SparkAuthFeatures
{
    /// <summary>Passkey sign-in (<see cref="SparkAuthenticationOptions.Passkeys"/>) is on.</summary>
    internal static bool Passkeys(IServiceProvider services)
        => (services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value.Passkeys ?? SparkPasskeys.Disabled)
            != SparkPasskeys.Disabled;

    /// <summary>The configured <see cref="SparkAuthenticationOptions.ExternalLoginLinking"/> mode.</summary>
    internal static SparkExternalLoginLinking ExternalLoginLinking(IServiceProvider services)
        => services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value.ExternalLoginLinking
            ?? SparkExternalLoginLinking.Disabled;
}
