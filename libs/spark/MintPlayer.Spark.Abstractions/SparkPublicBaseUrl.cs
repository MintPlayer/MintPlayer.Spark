using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The application's public origin for links that leave the request: account mails, invitations, notification
/// mails and pushes, share links. One rule for every library (it was copied four times): the configured
/// <c>Spark:Auth:PublicBaseUrl</c> (or an options value standing in for it), else, in Development only, the
/// request's own origin; else none.
/// </summary>
/// <remarks>
/// Outside Development the request's <c>Host</c> header is never used: on an anonymous endpoint it is chosen by
/// the caller, so a mailed link built from it can point a victim anywhere (password-reset poisoning).
/// </remarks>
public static class SparkPublicBaseUrl
{
    /// <summary>The configuration key every library reads.</summary>
    public const string ConfigurationKey = "Spark:Auth:PublicBaseUrl";

    /// <summary>
    /// <paramref name="configured"/>, else <c>configuration["Spark:Auth:PublicBaseUrl"]</c>, without a trailing
    /// slash; else the origin of <paramref name="context"/> when <paramref name="environment"/> is Development;
    /// else <see langword="null"/> (the caller refuses or skips, in its own words).
    /// </summary>
    public static string? Resolve(string? configured, IConfiguration? configuration, IHostEnvironment? environment, HttpContext? context)
    {
        var value = configured ?? configuration?[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(value))
            return value.TrimEnd('/');

        if (environment?.IsDevelopment() == true && context is not null)
            return RequestOrigin(context);

        return null;
    }

    /// <summary><c>{scheme}://{host}{pathBase}</c> of the request.</summary>
    public static string RequestOrigin(HttpContext context)
        => $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
}
