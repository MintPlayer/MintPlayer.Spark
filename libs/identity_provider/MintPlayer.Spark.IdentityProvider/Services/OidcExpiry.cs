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

    /// <summary>The incremental time series on an application counting the access tokens it was issued (PRD D9).</summary>
    public const string TokensTimeSeries = "INC:Tokens";

    /// <summary>
    /// Stores <paramref name="token"/> and stamps it to expire at its own <see cref="Models.OidcToken.ExpiresAt"/>.
    /// An access token also counts one issuance on its application's <see cref="TokensTimeSeries"/>: every grant
    /// stores its access token through here, so this is the one place that sees them all. Incremental, so
    /// concurrent issuances add up instead of conflicting, and the usage graph needs no extra documents.
    /// </summary>
    public static async Task StoreExpiringAsync(this IAsyncDocumentSession session, Models.OidcToken token, CancellationToken ct)
    {
        await session.StoreAsync(token, ct);
        Stamp(session, token, token.ExpiresAt);
        if (token.Type == Models.OidcTokenTypes.AccessToken && !string.IsNullOrEmpty(token.ApplicationId))
            session.IncrementalTimeSeriesFor(token.ApplicationId, TokensTimeSeries).Increment(token.CreatedAt, 1);
    }
}
