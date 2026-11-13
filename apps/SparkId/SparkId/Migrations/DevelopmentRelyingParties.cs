using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace SparkId.Migrations;

/// <summary>
/// The development-only relying parties the demo apps sign in with, shared by the migrations that seed them
/// (<see cref="M_202610081200_RelyingParties"/>: QnA, HR, Fleet; <see cref="M_202610101200_SeoTesterRelyingParty"/>:
/// SeoTester). One shape for all: a confidential client with a published development secret, the authorization-code
/// grant with PKCE, exact redirect URIs for Spark's <c>SparkId</c> OpenID Connect preset, and implicit consent.
/// </summary>
internal static class DevelopmentRelyingParties
{
    /// <summary>
    /// Stores the relying party unless a client with that id exists (by client id), so an administrator's edits
    /// survive. Does not save: the caller's migration commits. The client id is reserved like any registration's
    /// (O17), so nothing registered later can take it.
    /// </summary>
    public static async Task EnsureAsync(IAsyncDocumentSession session, string clientId, string displayName, string origin, string secret, CancellationToken ct)
    {
        var exists = await session.Query<OidcApplication>()
            .Where(a => a.ClientId == clientId, exact: true)
            .AnyAsync(ct);
        if (exists)
            return;

        try
        {
            await StoreAsync(session, clientId, displayName, origin, secret, ct);
        }
        catch (SparkValidationException)
        {
            // The query above rides an index, which may not have caught up with a client stored a moment ago; the
            // reservation is what says for certain that the client id is taken, and then the client exists.
        }
    }

    private static Task StoreAsync(IAsyncDocumentSession session, string clientId, string displayName, string origin, string secret, CancellationToken ct)
        => OidcClientIdReservation.StoreNewAsync(session, new OidcApplication
        {
            ClientId = clientId,
            DisplayName = displayName,
            ClientType = "confidential",
            Enabled = true,
            Secrets =
            [
                new ClientSecret
                {
                    Hash = ClientSecretHasher.Hash(secret),
                    Description = "Development only",
                    CreatedAt = DateTime.UtcNow,
                },
            ],
            AllowedGrantTypes = ["authorization_code"],
            RedirectUris = [$"{origin}/signin-SparkId"],
            PostLogoutRedirectUris = [$"{origin}/signout-callback-SparkId"],
            // D6: openid and profile are required, email is optional (the user may untick it).
            Scopes =
            [
                new OidcApplicationScope { Name = "openid", Required = true },
                new OidcApplicationScope { Name = "profile", Required = true },
                new OidcApplicationScope { Name = "email", Required = false },
            ],
            // The seeds are the demo's trusted first-party apps: Live, no members, consent implicit.
            Mode = OidcApplicationModes.Live,
            ConsentType = "implicit",
            RequirePkce = true,
        }, ct);
}
