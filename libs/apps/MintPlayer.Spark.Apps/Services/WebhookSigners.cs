using MintPlayer.Spark.Apps.Models;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Apps.Services;

/// <summary>The site-wide extra signers (<c>v2a</c>, D21) registered on <see cref="AppsBuilder.AddSigner"/>.</summary>
internal sealed class WebhookSigners
{
    private readonly IReadOnlyList<(IWebhookSigner Signer, string? PublicKey)> extra;

    public WebhookSigners(IReadOnlyList<(IWebhookSigner Signer, string? PublicKey)> extra)
    {
        this.extra = extra;
        PublicKeys = extra.Where(s => s.PublicKey is not null).Select(s => (s.Signer.Version, s.PublicKey!)).ToList();
    }

    /// <summary>The public keys receivers verify <c>v2a</c> with, by version.</summary>
    public IReadOnlyList<(string Version, string PublicKey)> PublicKeys { get; }

    /// <summary>
    /// The endpoint's current secret, its previous one while the rotation overlap lasts (so a receiver still on the old
    /// secret keeps verifying), then the extra signers.
    /// </summary>
    public IReadOnlyList<IWebhookSigner> For(WebhookEndpoint endpoint, WebhookSecretProtector secrets, DateTime nowUtc)
    {
        var signers = new List<IWebhookSigner> { new HmacWebhookSigner(secrets.Unprotect(endpoint.ProtectedSecret)) };
        if (endpoint.PreviousProtectedSecret is { } previous && endpoint.PreviousSecretExpiresUtc > nowUtc)
            signers.Add(new HmacWebhookSigner(secrets.Unprotect(previous)));
        signers.AddRange(extra.Select(s => s.Signer));
        return signers;
    }
}
