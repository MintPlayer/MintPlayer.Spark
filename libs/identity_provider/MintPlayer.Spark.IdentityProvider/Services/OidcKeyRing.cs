using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// The provider's signing keys (<c>docs/identity_provider_platform_PRD.md</c> D1 <c>OidcKeys</c>, D8 Keys, I10).
/// <list type="bullet">
/// <item>Stored in RavenDB, the private half protected with ASP.NET Data Protection
/// (<see cref="ProtectorPurpose"/>). Losing the Data Protection key ring makes them unreadable, and
/// startup then fails loudly: silently minting new keys would invalidate every token in flight and hide
/// the loss (spike S2).</item>
/// <item>One active RSA key (RS256, PS256) and one active EC P-256 key (ES256). A rotation publishes the
/// next key before it signs (<c>Keys:PrePublishDays</c>), so relying parties have it cached, and keeps a
/// retired key published while tokens it signed can still be presented (<c>Keys:RetainRetiredDays</c>).
/// The JWKS lists every published key; validation accepts all of them.</item>
/// <item>A key the old file-based setup used (<c>SigningKeyPath</c>) is imported once, with its key id,
/// so tokens issued before the ring existed keep validating.</item>
/// <item>The pairwise-subject salt lives here too (<c>OidcKeys/pairwise-salt</c>).</item>
/// </list>
/// Every instance reloads the ring every five minutes, so a rotation by one host reaches the others.
/// </summary>
internal sealed class OidcKeyRing(
    IDocumentStore store,
    IDataProtectionProvider dataProtection,
    IHostEnvironment environment,
    SparkIdentityProviderOptions options,
    ILogger<OidcKeyRing> logger)
{
    public const string ProtectorPurpose = "Spark.IdentityProvider.OidcKeys";
    private const string SaltId = "OidcKeys/pairwise-salt";
    private static readonly TimeSpan ReloadEvery = TimeSpan.FromMinutes(5);

    private sealed record Loaded(OidcKey Document, JsonWebKey PrivateKey);

    private volatile IReadOnlyList<Loaded> keys = [];
    private DateTime loadedAt = DateTime.MinValue;
    private readonly SemaphoreSlim gate = new(1, 1);

    private IDataProtector Protector => dataProtection.CreateProtector(ProtectorPurpose);

    /// <summary>A ring over fixed keys and no database, for unit tests of token generation and validation.</summary>
    internal static OidcKeyRing InMemory(RsaSecurityKey rsa)
    {
        var ring = new OidcKeyRing(null!, null!, null!, new SparkIdentityProviderOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger<OidcKeyRing>.Instance);
        var kid = rsa.KeyId ?? "test-rsa";
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.Rsa?.ExportParameters(true) ?? rsa.Parameters) { KeyId = kid });
        ring.keys = [new Loaded(new OidcKey { Id = "OidcKeys/" + kid, Kid = kid, Use = "sig", Algorithm = SecurityAlgorithms.RsaSha256, State = OidcKeyStates.Active, CreatedAt = DateTime.UtcNow }, jwk)];
        ring.loadedAt = DateTime.MaxValue;
        return ring;
    }

    /// <summary>The algorithms the ring can sign with, for discovery.</summary>
    public IReadOnlyList<string> SupportedAlgorithms => [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256];

    /// <summary>Every published key (next, active, retired): what a token from this provider may be signed with.</summary>
    public IReadOnlyList<SecurityKey> ValidationKeys => Current().Select(k => (SecurityKey)k.PrivateKey).ToList();

    /// <summary>The JWKS: every published key, public half only.</summary>
    public JsonWebKeySet Jwks
    {
        get
        {
            var set = new JsonWebKeySet();
            foreach (var key in Current())
                set.Keys.Add(PublicOnly(key.PrivateKey, key.Document));
            return set;
        }
    }

    /// <summary>
    /// The credentials for <paramref name="algorithm"/> (a client's <c>*_signed_response_alg</c>): the
    /// active EC key for ES256, the active RSA key for RS256 (default) and PS256.
    /// </summary>
    public SigningCredentials GetSigningCredentials(string? algorithm)
    {
        var wantsEc = algorithm == SecurityAlgorithms.EcdsaSha256;
        var active = Current().FirstOrDefault(k => k.Document.State == OidcKeyStates.Active && (k.PrivateKey.Kty == "EC") == wantsEc)
            ?? throw new InvalidOperationException("The identity provider has no active signing key. Initialize the key ring at startup.");
        var alg = wantsEc ? SecurityAlgorithms.EcdsaSha256
            : algorithm == SecurityAlgorithms.RsaSsaPssSha256 ? SecurityAlgorithms.RsaSsaPssSha256
            : SecurityAlgorithms.RsaSha256;
        return new SigningCredentials(active.PrivateKey, alg);
    }

    /// <summary>Loads the ring, creating what is missing. Called once at startup; throws when stored keys cannot be read.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        using var session = store.OpenAsyncSession();
        var stored = await LoadAllAsync(session, ct);

        if (!stored.Any(k => k.Document.State == OidcKeyStates.Active && k.PrivateKey.Kty == "RSA"))
            stored.Add(await CreateAsync(session, "RSA", ImportLegacyRsa(), ct));
        if (!stored.Any(k => k.Document.State == OidcKeyStates.Active && k.PrivateKey.Kty == "EC"))
            stored.Add(await CreateAsync(session, "EC", null, ct));

        var salt = await session.LoadAsync<OidcKey>(SaltId, ct);
        if (salt is null)
        {
            salt = new OidcKey
            {
                Id = SaltId, Kid = "pairwise-salt", Use = "salt", Algorithm = "HMAC", State = OidcKeyStates.Active,
                ProtectedPrivateJwk = Protector.Protect(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))), CreatedAt = DateTime.UtcNow,
            };
            await session.StoreAsync(salt, ct);
        }
        OidcSubjects.Salt = Convert.FromBase64String(Unprotect(salt));

        await session.SaveChangesAsync(ct);
        keys = stored;
        loadedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Advances the rotation schedule (Keys:RotationDays): publishes the next key ahead of time, promotes
    /// it when the active one is due, and drops retired keys past their retention. Returns what changed.
    /// <paramref name="force"/> rotates now: a new key signs immediately and the old one retires.
    /// </summary>
    public async Task<IReadOnlyList<string>> RotateAsync(bool force, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var changes = new List<string>();
            var now = DateTime.UtcNow;
            var rotation = TimeSpan.FromDays(Math.Max(1, options.Keys.RotationDays));
            var prePublish = TimeSpan.FromDays(Math.Max(0, options.Keys.PrePublishDays));
            var retain = TimeSpan.FromDays(Math.Max(1, options.Keys.RetainRetiredDays));

            using var session = store.OpenAsyncSession();
            var all = await LoadAllAsync(session, ct);
            foreach (var kty in new[] { "RSA", "EC" })
            {
                var active = all.FirstOrDefault(k => k.Document.State == OidcKeyStates.Active && k.PrivateKey.Kty == kty);
                var next = all.FirstOrDefault(k => k.Document.State == OidcKeyStates.Next && k.PrivateKey.Kty == kty);
                var activeSince = active?.Document.ActivatedAt ?? active?.Document.CreatedAt ?? now;

                if (next is null && (force || activeSince + rotation - prePublish <= now))
                {
                    next = await CreateAsync(session, kty, null, ct, OidcKeyStates.Next);
                    all.Add(next);
                    changes.Add($"published {next.Document.Kid}");
                }

                if (next is not null && (force || (activeSince + rotation <= now && next.Document.CreatedAt + prePublish <= now)))
                {
                    if (active is not null)
                    {
                        active.Document.State = OidcKeyStates.Retired;
                        active.Document.RetiredAt = now;
                    }
                    next.Document.State = OidcKeyStates.Active;
                    next.Document.ActivatedAt = now;
                    changes.Add($"activated {next.Document.Kid}");
                }
            }

            foreach (var retired in all.Where(k => k.Document.State == OidcKeyStates.Retired && k.Document.RetiredAt + retain <= now).ToList())
            {
                session.Delete(retired.Document);
                all.Remove(retired);
                changes.Add($"removed {retired.Document.Kid}");
            }

            if (changes.Count > 0)
            {
                await new OidcAudit(options).RecordAsync(session, OidcAuditKinds.KeyRotated, actorId: null,
                    details: new Dictionary<string, string> { ["changes"] = string.Join("; ", changes), ["forced"] = force.ToString() }, ct: ct);
                await session.SaveChangesAsync(ct);
                logger.LogInformation("Identity provider key rotation: {Changes}.", string.Join("; ", changes));
            }

            keys = all;
            loadedAt = now;
            return changes;
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<Loaded> Current()
    {
        if (DateTime.UtcNow - loadedAt > ReloadEvery && gate.Wait(0))
        {
            try
            {
                using var session = store.OpenSession();
                // The whole collection, filtered here: a where clause would be answered by an auto-index.
                var reloaded = session.Advanced.RawQuery<OidcKey>("from OidcKeys").ToList().Where(k => k.Use == "sig").Select(Open).ToList();
                if (reloaded.Count > 0)
                    keys = reloaded;
                loadedAt = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not CryptographicException)
            {
                // A reload is a refresh; the keys already held keep working.
                logger.LogWarning(ex, "Reloading the identity provider's key ring failed; keeping the loaded keys.");
                loadedAt = DateTime.UtcNow;
            }
            finally
            {
                gate.Release();
            }
        }
        return keys;
    }

    private async Task<List<Loaded>> LoadAllAsync(IAsyncDocumentSession session, CancellationToken ct)
    {
        // A collection scan, not an index read: the ring is a handful of documents, and a stale view
        // could miss the key that is signing. Filtered in memory, because any where clause, even on a
        // collection query, is answered by an auto-index (which made a rotation right after another one
        // load no active key).
        var docs = await session.Advanced.AsyncRawQuery<OidcKey>("from OidcKeys").ToListAsync(ct);
        return docs.Where(d => d.Use == "sig").Select(Open).ToList();
    }

    private Loaded Open(OidcKey document)
    {
        try
        {
            return new Loaded(document, new JsonWebKey(Unprotect(document)) { KeyId = document.Kid });
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"The identity provider's signing key '{document.Kid}' cannot be decrypted. The ASP.NET Data Protection key ring "
                + "it was protected with is not available (Spark:DataProtection). Restore that key ring; deleting the OidcKeys "
                + "documents instead makes every issued token invalid.", ex);
        }
    }

    private string Unprotect(OidcKey document) => Protector.Unprotect(document.ProtectedPrivateJwk);

    private async Task<Loaded> CreateAsync(IAsyncDocumentSession session, string kty, (RSA Rsa, string Kid)? import, CancellationToken ct, string state = OidcKeyStates.Active)
    {
        JsonWebKey jwk;
        string kid;
        if (kty == "EC")
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            kid = "ec-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(9));
            jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(ec) { KeyId = kid });
        }
        else
        {
            var rsa = import?.Rsa ?? RSA.Create(2048);
            kid = import?.Kid ?? "rsa-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(9));
            jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = kid });
        }

        var now = DateTime.UtcNow;
        var document = new OidcKey
        {
            Id = "OidcKeys/" + kid,
            Kid = kid,
            Use = "sig",
            Algorithm = kty == "EC" ? SecurityAlgorithms.EcdsaSha256 : SecurityAlgorithms.RsaSha256,
            State = state,
            ProtectedPrivateJwk = Protector.Protect(System.Text.Json.JsonSerializer.Serialize(jwk, JwkJson)),
            CreatedAt = now,
            ActivatedAt = state == OidcKeyStates.Active ? now : null,
        };
        await session.StoreAsync(document, ct);
        return new Loaded(document, jwk);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JwkJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The key of the former file-based setup, when one is configured and present.</summary>
    private (RSA Rsa, string Kid)? ImportLegacyRsa()
    {
        var path = Path.IsPathRooted(options.SigningKeyPath) ? options.SigningKeyPath : Path.Combine(environment.ContentRootPath, options.SigningKeyPath);
        if (!File.Exists(path))
            return null;
        var legacy = new OidcSigningKeyService(environment, options.SigningKeyPath);
        logger.LogInformation("Importing the identity provider's signing key from {Path} into the key ring.", path);
        return (legacy.GetSigningKey().Rsa, legacy.GetSigningKey().KeyId);
    }

    private static JsonWebKey PublicOnly(JsonWebKey key, OidcKey document)
    {
        var copy = new JsonWebKey
        {
            Kty = key.Kty, Kid = document.Kid, Use = "sig", Alg = document.Algorithm,
            N = key.N, E = key.E, Crv = key.Crv, X = key.X, Y = key.Y,
        };
        return copy;
    }
}
