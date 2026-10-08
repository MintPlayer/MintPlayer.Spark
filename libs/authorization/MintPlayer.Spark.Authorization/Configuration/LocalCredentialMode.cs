using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// The configured <see cref="SparkLocalCredentials"/> mode, read the one way every account endpoint's
/// <c>IsEnabled</c> reads it (endpoints generator completion D5).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ Called at map time with the application's <b>root</b> provider, so it reads
/// <see cref="IOptions{TOptions}"/> — never <c>IOptionsSnapshot</c> or a scoped service, which the root
/// provider refuses (or, without scope validation, silently hands out as a singleton).
/// </para>
/// <para>
/// Without registered options the mode is <see cref="SparkLocalCredentials.Disabled"/>, the default of
/// <see cref="SparkAuthenticationOptions.LocalCredentials"/>: an unconfigured application maps no
/// password surface.
/// </para>
/// </remarks>
internal static class LocalCredentialMode
{
    internal static SparkLocalCredentials Get(IServiceProvider services)
        => services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value.LocalCredentials
            ?? SparkLocalCredentials.Disabled;

    /// <summary><see cref="SparkLocalCredentials.Full"/>: self-service sign-up and its mail trigger.</summary>
    internal static bool IsFull(IServiceProvider services)
        => Get(services) == SparkLocalCredentials.Full;

    /// <summary>
    /// <see cref="SparkLocalCredentials.Full"/> or <see cref="SparkLocalCredentials.SignInOnly"/>: accounts
    /// have passwords, so the password surface (forgot, reset, change) and the email change exist.
    /// </summary>
    internal static bool HasPasswords(IServiceProvider services)
        => Get(services) != SparkLocalCredentials.Disabled;
}
