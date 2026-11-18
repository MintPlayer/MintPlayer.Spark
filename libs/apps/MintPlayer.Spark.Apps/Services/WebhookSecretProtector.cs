using Microsoft.AspNetCore.DataProtection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Apps.Services;

/// <summary>
/// Endpoint secrets at rest (D15): <c>sdp1:</c> + Data Protection, the same scheme as the user store's secrets. The
/// key ring rotates on its own; a database dump without it holds no usable secret.
/// </summary>
internal sealed partial class WebhookSecretProtector
{
    private const string Marker = "sdp1:";

    [Inject] private readonly IDataProtectionProvider dataProtection;

    private IDataProtector Protector => dataProtection.CreateProtector("MintPlayer.Spark.Apps", "WebhookEndpointSecret");

    public string Protect(WebhookSecret secret) => Marker + Protector.Protect(secret.ToString());

    public WebhookSecret Unprotect(string stored)
    {
        if (!stored.StartsWith(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("A webhook secret is stored without its sdp1: protection.");
        return WebhookSecret.Parse(Protector.Unprotect(stored[Marker.Length..]));
    }
}
