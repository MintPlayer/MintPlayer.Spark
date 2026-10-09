using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Links into the developer portal for mails (<c>docs/identity_provider_platform_PRD.md</c> D2, D3),
/// under the same rule as the account mails (<c>SparkAuthLinkBuilder</c>): the configured public base
/// URL (<c>Spark:Auth:PublicBaseUrl</c>), the request's own origin in Development only, and otherwise
/// a refusal. A link built from the Host header of an invitation request would let its sender point
/// the invitee anywhere.
/// </summary>
internal sealed class OidcPortalLinks(
    IOptions<SparkAuthenticationOptions> authOptions,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null)
{
    /// <summary>The developer portal's home (<c>/developers</c>).</summary>
    public string Portal(HttpContext context) => BaseUrl(context) + "/developers";

    /// <summary>The page that accepts an invitation (<c>/developers/invitations/{token}</c>).</summary>
    public string Invitation(HttpContext context, string applicationId, string token)
        => QueryHelpers.AddQueryString(
            BaseUrl(context) + "/developers/invitations/" + Uri.EscapeDataString(token),
            "app", applicationId);

    private string BaseUrl(HttpContext context)
    {
        var configured = authOptions.Value.Links.PublicBaseUrl ?? configuration?["Spark:Auth:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');

        if (environment?.IsDevelopment() == true)
            return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";

        throw new InvalidOperationException(
            "The identity provider cannot build a developer-portal link: no public base URL is configured. "
            + "Set 'Spark:Auth:PublicBaseUrl' (e.g. https://id.example.com). Outside Development the request's "
            + "Host header is not used for mailed links.");
    }
}
