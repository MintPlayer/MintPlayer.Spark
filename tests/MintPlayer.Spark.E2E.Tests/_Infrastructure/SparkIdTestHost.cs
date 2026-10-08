using System.Text.Json.Nodes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// Runs SparkId, the demo identity provider, on <see cref="SparkAppTestHost"/>
/// (<c>docs/identity_provider_platform_PRD.md</c> I0). HR, Fleet and QnA are its relying parties; in
/// the E2E suite it issues the machine tokens Fleet validates.
/// </summary>
/// <remarks>
/// The issuer runs on <b>http</b>: a resource server fetches the discovery document from the issuer
/// itself, and over https that means trusting the host's development certificate, which a CI runner
/// does not.
/// </remarks>
public sealed class SparkIdTestHost : SparkAppTestHost
{
    public static readonly SparkAppDescriptor SparkId = new(
        AppName: "SparkId",
        ProjectDirectory: Path.Combine("apps", "SparkId", "SparkId"),
        ProjectFileName: "SparkId.csproj",
        DatabasePrefix: "SparkIdE2E",
        CoverageSlug: "sparkid")
    {
        UsesMailPickup = true,
    };

    public SparkIdTestHost() : base(SparkId) { }

    /// <summary>The issuer: the plain-http base URL.</summary>
    public string Issuer => AppHttpUrl;

    protected override async Task ConfigureAppSettings(JsonObject settings, SparkAppHostContext context)
    {
        // The provider auto-generates a signing key only in Development, and deliberately: a key
        // that materialises on first use in production is a key nobody backed up, and it silently
        // invalidates every token still in flight when the host restarts. Tests are not Development,
        // so they supply one, which also exercises the configured-key path.
        var signingKeyFileName = $"oidc-signing-key.{EnvironmentName}.json";
        var signingKeyFile = Path.Combine(context.ProjectDirectory, signingKeyFileName);
        RegisterTemporaryFile(signingKeyFile);
        await File.WriteAllTextAsync(signingKeyFile, NewSigningKeyJson());

        settings["SparkIdentityProvider"] = new JsonObject
        {
            ["Issuer"] = $"http://localhost:{context.HttpPort}",
            ["SigningKeyPath"] = signingKeyFileName,
        };

        // The developer portal's mails (invitations, developer decisions) carry links, and outside
        // Development the provider builds them only from a configured public base URL, never from the
        // request's Host header (OidcPortalLinks). Without it an invitation is recorded but never mailed.
        ((JsonObject)settings["Spark"]!)["Auth"] = new JsonObject { ["PublicBaseUrl"] = $"http://localhost:{context.HttpPort}" };
    }

    /// <summary>
    /// Ensures the identity scope <paramref name="name"/> exists, as an administrator would create it.
    /// SparkId's own seed (<c>M_202610081200_RelyingParties</c>) writes <c>openid</c>, <c>profile</c> and
    /// <c>email</c> in Development only, and tests are not Development.
    /// </summary>
    public async Task SeedIdentityScopeAsync(string name, List<string> claimTypes, bool required)
    {
        using var appStore = OpenAppStore();
        using var session = appStore.OpenAsyncSession();

        var id = OidcScopeCatalog.ResourceId(name);
        if (await session.Advanced.ExistsAsync(id))
            return;
        await session.StoreAsync(new OidcResource
        {
            Id = id,
            Kind = OidcResourceKinds.Identity,
            Name = name,
            ClaimTypes = claimTypes,
            Required = required,
        });
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Registers the API scope <paramref name="scopeName"/> on the API resource named <paramref name="audience"/>
    /// (created if missing). With <paramref name="autoApprove"/>, an application of any team may add the
    /// scope without the owner's approval (D4).
    /// </summary>
    public async Task SeedApiScopeAsync(string audience, string scopeName, bool autoApprove)
    {
        using var appStore = OpenAppStore();
        using var session = appStore.OpenAsyncSession();

        var resourceId = OidcScopeCatalog.ResourceId(audience);
        var api = await session.LoadAsync<OidcResource>(resourceId);
        if (api is null)
        {
            api = new OidcResource { Id = resourceId, Kind = OidcResourceKinds.Api, Name = audience, AutoApprove = autoApprove };
            await session.StoreAsync(api);
        }
        api.Scopes.Add(new OidcApiScope { Name = scopeName });
        await session.SaveChangesAsync();
    }

    /// <summary>An RSA key in the shape <c>OidcSigningKeyService</c> reads: base64url RSA parameters.</summary>
    private static string NewSigningKeyJson()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var p = rsa.ExportParameters(true);
        static string B64(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            N = B64(p.Modulus!),
            E = B64(p.Exponent!),
            D = B64(p.D!),
            P = B64(p.P!),
            Q = B64(p.Q!),
            DP = B64(p.DP!),
            DQ = B64(p.DQ!),
            QI = B64(p.InverseQ!),
        });
    }

    /// <summary>
    /// Registers a confidential <c>client_credentials</c> application and an API scope on the API
    /// resource named <paramref name="audience"/> (created if missing), then returns the client secret
    /// and the scope's full name (<c>{audience}.{scopeSuffix}</c>).
    /// <para>
    /// The <c>group</c> claim is the entire authorization integration: a machine token carrying
    /// <c>group = "{group}"</c> is governed by the resource server's <c>security.json</c> like a person,
    /// because group membership is resolved from claims and nothing else knows what a client is.
    /// </para>
    /// </summary>
    public async Task<(string Secret, string Scope)> SeedMachineClientAsync(string clientId, string scopeSuffix, string audience, string group)
    {
        var secret = $"S{Guid.NewGuid():N}!a";
        var scopeName = $"{audience}.{scopeSuffix}";

        using var appStore = OpenAppStore();
        using var session = appStore.OpenAsyncSession();

        // The audience is the API resource's name: a token carrying one of its scopes is addressed
        // to it, not to everything the issuer serves.
        var resourceId = OidcScopeCatalog.ResourceId(audience);
        var api = await session.LoadAsync<OidcResource>(resourceId);
        if (api is null)
        {
            api = new OidcResource { Id = resourceId, Kind = OidcResourceKinds.Api, Name = audience };
            await session.StoreAsync(api);
        }
        api.Scopes.Add(new OidcApiScope { Name = scopeName });

        await session.StoreAsync(new OidcApplication
        {
            ClientId = clientId,
            DisplayName = clientId,
            ClientType = "confidential",
            Enabled = true,
            Secrets = [new ClientSecret { Hash = ClientSecretHasher.Hash(secret), CreatedAt = DateTime.UtcNow }],
            AllowedGrantTypes = ["client_credentials"],
            Scopes = [new OidcApplicationScope { Name = scopeName }],
            Mode = OidcApplicationModes.Live,
            Claims = [new ClientClaim { Type = "group", Value = group }],
        });

        await session.SaveChangesAsync();
        await appStore.WaitForIndexingAsync(TestDatabase);

        return (secret, scopeName);
    }
}
