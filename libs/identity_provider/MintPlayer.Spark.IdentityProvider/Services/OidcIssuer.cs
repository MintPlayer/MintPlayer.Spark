using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.IdentityProvider.Configuration;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The issuer identifier for this provider.
/// <para>
/// Every issuance and validation path resolves it here, because the two must agree and
/// because the previous approach — deriving it from the request's <c>Host</c> header — put
/// the value under the caller's control. A forged <c>Host</c> minted tokens claiming a
/// different issuer, signed with the real key.
/// </para>
/// </summary>
internal static class OidcIssuer
{
    public static string Resolve(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<SparkIdentityProviderOptions>();

        if (!string.IsNullOrWhiteSpace(options.Issuer))
            return options.Issuer.TrimEnd('/');

        // Fail closed rather than fall back to the header: an unset issuer in production is
        // the vulnerable configuration, and silently working is how it would stay unset.
        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "SparkIdentityProviderOptions.Issuer must be set outside Development. Deriving it " +
                "from the Host header lets a caller mint tokens claiming any issuer, signed with " +
                "this provider's key.");
        }

        return $"{context.Request.Scheme}://{context.Request.Host}";
    }
}
