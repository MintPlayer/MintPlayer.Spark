using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Boots the OIDC provider in-process and seeds the records its endpoints reason about.
/// <para>
/// Everything below M12.2 in the plan was fixed by reading code and is unverified by anything
/// that speaks HTTP — two Criticals, a one-click account takeover and a cross-client
/// disclosure among them. This is where that changes: the tests built on this fixture are the
/// first to exercise <c>/connect/*</c> at all.
/// </para>
/// <para>
/// In-process on <c>TestServer</c> rather than a hosted demo app, so there is no subprocess,
/// no Angular build, and no shared state between cases. Note that <c>TestServer</c>'s
/// <c>HttpClient</c> does not manage cookies — anything cookie-driven (login, consent) must
/// thread them explicitly.
/// </para>
/// </summary>
public abstract class OidcTestHost : SparkTestDriver
{
    protected const string Issuer = "https://idp.test";

    private SparkEndpointFactory<OidcTestContext>? _factory;

    protected SparkEndpointFactory<OidcTestContext> Factory =>
        _factory ??= new SparkEndpointFactory<OidcTestContext>(
            Store,
            models: [],
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>();
                spark.AddIdentityProvider(options =>
                {
                    // Pinned rather than derived from the Host header: O7's fix makes this
                    // required outside Development, and pinning it here means the tests also
                    // assert the value the endpoints actually stamp.
                    options.Issuer = Issuer;
                    options.SigningKeyPath = Path.Combine(
                        Path.GetTempPath(), "spark-oidc-test-" + Guid.NewGuid().ToString("N") + ".json");
                });
            },
            // Development so the provider generates its own signing key. Production refusing to
            // do that is the correct behaviour and is covered separately by R-K1.
            environment: "Development");

    protected HttpClient Client => Factory.CreateClient();

    public override async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();

        await base.DisposeAsync();
    }

    /// <summary>
    /// Seeds an application. Defaults describe the ordinary case — a confidential web client
    /// doing the authorization-code flow with PKCE — so each test names only what it is about.
    /// </summary>
    protected async Task<OidcApplication> SeedApplicationAsync(
        string clientId,
        string? secret = "s3cret-value-for-tests",
        string[]? redirectUris = null,
        string[]? allowedScopes = null,
        string[]? grantTypes = null,
        string[]? postLogoutRedirectUris = null,
        bool enabled = true,
        bool requirePkce = false,
        string consentType = "explicit",
        string clientType = "confidential")
    {
        var app = new OidcApplication
        {
            ClientId = clientId,
            DisplayName = clientId,
            ClientType = clientType,
            Enabled = enabled,
            RequirePkce = requirePkce,
            ConsentType = consentType,
            RedirectUris = [.. redirectUris ?? [$"https://{clientId}.test/cb"]],
            PostLogoutRedirectUris = [.. postLogoutRedirectUris ?? []],
            AllowedScopes = [.. allowedScopes ?? ["openid", "profile"]],
            AllowedGrantTypes = [.. grantTypes ?? ["authorization_code"]],
        };

        if (secret != null)
            app.Secrets.Add(new ClientSecret { Hash = ClientSecretHasher.Hash(secret) });

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(app);
        await session.SaveChangesAsync();
        return app;
    }

    protected async Task<SparkUser> SeedUserAsync(string email)
    {
        var user = new SparkUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
        };

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(user);
        await session.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Writes an authorization request straight to storage, standing in for a completed
    /// <c>/connect/authorize</c> hop. Returns the opaque handle the browser would carry.
    /// <para>
    /// Seeding it rather than driving the real hop is deliberate for the consent tests: those
    /// assert what <c>/connect/consent</c> does with a handle, and going through the login
    /// pages first would make a consent failure indistinguishable from a login failure.
    /// </para>
    /// </summary>
    protected async Task<string> SeedAuthorizationRequestAsync(
        OidcApplication app,
        string subject,
        string[]? scopes = null,
        string? redirectUri = null,
        string status = "pending",
        DateTime? expiresAt = null)
    {
        var handle = OidcRequestReference.GenerateValue();
        var request = new OidcAuthorizationRequest
        {
            Id = OidcRequestReference.DocumentId(handle),
            ApplicationId = app.Id!,
            Subject = subject,
            RedirectUri = redirectUri ?? app.RedirectUris[0],
            Scopes = [.. scopes ?? ["openid"]],
            Status = status,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(10),
        };

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(request);
        await session.SaveChangesAsync();
        return handle;
    }
}

/// <summary>Minimal context: these tests exercise <c>/connect/*</c>, not persistent objects.</summary>
public sealed class OidcTestContext : SparkContext
{
}
