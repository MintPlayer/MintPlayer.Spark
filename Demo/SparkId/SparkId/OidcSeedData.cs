using System.Security.Cryptography;
using System.Text;
using Raven.Client.Documents;
using SparkId.Entities;

namespace SparkId;

internal static class OidcSeedData
{
    /// <summary>
    /// Seeds default OIDC scopes and development client registrations if they don't exist.
    /// </summary>
    public static async Task SeedAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();

        // Seed default scopes
        await SeedScopeAsync(session, "openid", "Your identity", "Access your user identifier",
            ["sub"], required: true);
        await SeedScopeAsync(session, "profile", "Your profile", "Access your name and profile information",
            ["name", "family_name", "given_name", "preferred_username", "picture", "locale", "updated_at"]);
        await SeedScopeAsync(session, "email", "Your email", "Access your email address",
            ["email", "email_verified"]);
        await SeedScopeAsync(session, "roles", "Your roles", "Access your assigned roles",
            ["role"], emphasize: true);

        // Seed development client registrations
        await SeedClientAsync(session, new OidcApplication
        {
            ClientId = "hr-app",
            Secrets =
            [
                new ClientSecret
                {
                    Hash = HashSecret("hr-dev-secret"),
                    Description = "Development secret",
                    CreatedAt = DateTime.UtcNow,
                },
            ],
            DisplayName = "HR Application",
            ClientType = "confidential",
            ConsentType = "implicit", // auto-approve for dev
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RedirectUris = ["https://localhost:5005/spark/auth/oidc-callback"],
            PostLogoutRedirectUris = ["https://localhost:5005"],
            AllowedCorsOrigins = ["https://localhost:5005"],
            AllowedScopes = ["openid", "profile", "email", "roles"],
            RequirePkce = true,
            Enabled = true,
        });

        await SeedClientAsync(session, new OidcApplication
        {
            ClientId = "fleet-app",
            Secrets =
            [
                new ClientSecret
                {
                    Hash = HashSecret("fleet-dev-secret"),
                    Description = "Development secret",
                    CreatedAt = DateTime.UtcNow,
                },
            ],
            DisplayName = "Fleet Application",
            ClientType = "confidential",
            ConsentType = "implicit", // auto-approve for dev
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            RedirectUris = ["https://localhost:5003/spark/auth/oidc-callback"],
            PostLogoutRedirectUris = ["https://localhost:5003"],
            AllowedCorsOrigins = ["https://localhost:5003"],
            AllowedScopes = ["openid", "profile", "email", "roles"],
            RequirePkce = true,
            Enabled = true,
        });

        await session.SaveChangesAsync();
    }

    private static async Task SeedScopeAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        string name, string displayName, string description,
        List<string> claimTypes, bool required = false, bool emphasize = false)
    {
        var existing = await session.Query<OidcScope>()
            .Where(s => s.Name == name)
            .FirstOrDefaultAsync();

        if (existing == null)
        {
            await session.StoreAsync(new OidcScope
            {
                Name = name,
                DisplayName = displayName,
                Description = description,
                ClaimTypes = claimTypes,
                Required = required,
                Emphasize = emphasize,
                ShowInDiscoveryDocument = true,
                Enabled = true,
            });
            Console.WriteLine($"Seeded OIDC scope: {name}");
        }
    }

    private static async Task SeedClientAsync(
        Raven.Client.Documents.Session.IAsyncDocumentSession session,
        OidcApplication client)
    {
        var existing = await session.Query<OidcApplication>()
            .Where(a => a.ClientId == client.ClientId)
            .FirstOrDefaultAsync();

        if (existing == null)
        {
            await session.StoreAsync(client);
            Console.WriteLine($"Seeded OIDC client: {client.ClientId} ({client.DisplayName})");
        }
    }

    internal static string HashSecret(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
