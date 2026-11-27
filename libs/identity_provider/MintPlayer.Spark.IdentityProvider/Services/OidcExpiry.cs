using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Marks a record for the store to delete itself (<c>docs/identity_provider_platform_PRD.md</c> D1), through
/// <see cref="ISparkSession.SetExpiry"/>: RavenDB's <c>@expires</c>, the expiration sweep elsewhere (#536 D47).
/// <para>
/// Housekeeping only. The expiration sweep runs every 36 hours at best on the Community licence,
/// so no decision may rest on the document being gone: every read checks the document's own expiry.
/// </para>
/// </summary>
internal static class OidcExpiry
{
    /// <summary>Marks a record tracked by <paramref name="session"/> to be deleted at <paramref name="expiresAt"/>.</summary>
    public static void Stamp(ISparkSession session, object entity, DateTime expiresAt)
        => session.SetExpiry(entity, new DateTimeOffset(DateTime.SpecifyKind(expiresAt.ToUniversalTime(), DateTimeKind.Utc)));

    /// <summary>
    /// The incremental time series on an application counting the access tokens it was issued (PRD D9). RavenDB only
    /// (#536 D47): nothing reads it, and on another store the token rows themselves are the record.
    /// </summary>
    public const string TokensTimeSeries = "INC:Tokens";

    /// <summary>
    /// Stores <paramref name="token"/> and stamps it to expire at its own <see cref="Models.OidcToken.ExpiresAt"/>.
    /// On RavenDB an access token also counts one issuance on its application's <see cref="TokensTimeSeries"/>: every
    /// grant stores its access token through here, so this is the one place that sees them all. Incremental, so
    /// concurrent issuances add up instead of conflicting.
    /// </summary>
    public static async Task StoreExpiringAsync(this ISparkSession session, Models.OidcToken token, CancellationToken ct)
    {
        await session.StoreAsync(token, ct);
        Stamp(session, token, token.ExpiresAt);
        if (token.Type == Models.OidcTokenTypes.AccessToken && !string.IsNullOrEmpty(token.ApplicationId)
            && session.Native is Raven.Client.Documents.Session.IAsyncDocumentSession raven)
            raven.IncrementalTimeSeriesFor(token.ApplicationId, TokensTimeSeries).Increment(token.CreatedAt, 1);
    }
}
