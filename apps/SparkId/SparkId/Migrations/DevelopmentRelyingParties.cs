using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

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
    /// Stores the relying party and reserves its client id, unless an existing application holds that client id, so
    /// an administrator's edits survive. Does not save: the caller's migration commits.
    /// </summary>
    /// <remarks>
    /// The client-id reservation is both the existence check and the only way the provider finds the application by
    /// its client id (#536 D47), on every store; a collection query would build an auto index on RavenDB (D42).
    /// </remarks>
    public static async Task EnsureAsync(ISparkSession session, string clientId, string displayName, string origin, string secret, CancellationToken ct)
    {
        if (await session.GetReservationAsync(OidcClientIdReservation.Key(clientId), ct) is { } holder
            && await session.ExistsAsync(typeof(OidcApplication), holder, ct))
            return;

        var app = new OidcApplication
        {
            Id = Id<OidcApplication>.FromTrusted("oidcapplications/" + Guid.NewGuid().ToString("N")),
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
        };
        await OidcClientIdReservation.ReserveAsync(session, app, ct);
        await session.StoreAsync(app, app.Id.Value, ct);
    }
}
