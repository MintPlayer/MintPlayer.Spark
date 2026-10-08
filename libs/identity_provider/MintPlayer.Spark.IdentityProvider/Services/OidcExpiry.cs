using Raven.Client;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Stamps <c>@expires</c> so RavenDB deletes a document itself (<c>docs/identity_provider_platform_PRD.md</c> D1).
/// <para>
/// Housekeeping only. The expiration sweep runs every 36 hours at best on the Community licence,
/// so no decision may rest on the document being gone: every read checks the document's own expiry.
/// </para>
/// </summary>
internal static class OidcExpiry
{
    /// <summary>Marks a document stored in <paramref name="session"/> to be deleted at <paramref name="expiresAt"/>.</summary>
    public static void Stamp(IAsyncDocumentSession session, object entity, DateTime expiresAt)
        => session.Advanced.GetMetadataFor(entity)[Constants.Documents.Metadata.Expires] = expiresAt.ToUniversalTime();

    /// <summary>Stores <paramref name="token"/> and stamps it to expire at its own <see cref="Models.OidcToken.ExpiresAt"/>.</summary>
    public static async Task StoreExpiringAsync(this IAsyncDocumentSession session, Models.OidcToken token, CancellationToken ct)
    {
        await session.StoreAsync(token, ct);
        Stamp(session, token, token.ExpiresAt);
    }
}
