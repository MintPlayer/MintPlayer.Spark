using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Actions;

/// <summary>
/// Validation for the OIDC client admin screen.
/// <para>
/// Everything enforced here is something the protocol endpoints already assume. The audit found
/// each of these assumptions failing quietly rather than loudly — an unknown grant type that
/// grants nothing, a scope that vanishes from the issued token, a duplicate client id that makes
/// "which application is this?" a matter of index ordering. A configuration screen that accepts
/// those hands the operator a client that looks configured and does not work, with nothing
/// anywhere saying why. Refusing at the point of entry is the only place the operator can act on
/// the answer.
/// </para>
/// </summary>
public partial class OidcApplicationActions : DefaultPersistentObjectActions<OidcApplication>
{
    private static readonly string[] SupportedGrantTypes =
        ["authorization_code", "refresh_token", "client_credentials"];

    public override async Task OnBeforeSaveAsync(PersistentObject obj, OidcApplication entity)
    {
        if (string.IsNullOrWhiteSpace(entity.ClientId))
            throw new InvalidOperationException("Client id is required.");

        ValidateRedirectUris(entity.RedirectUris, nameof(entity.RedirectUris));
        ValidateRedirectUris(entity.PostLogoutRedirectUris, nameof(entity.PostLogoutRedirectUris));
        ValidateGrantTypes(entity);
        HashAnyNewSecrets(entity);

        await base.OnBeforeSaveAsync(obj, entity);
    }

    /// <summary>
    /// A redirect URI is compared verbatim at authorize time, so anything that would not match
    /// exactly is a client that can never complete a flow.
    /// </summary>
    private static void ValidateRedirectUris(List<string> uris, string field)
    {
        foreach (var uri in uris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                throw new InvalidOperationException($"{field}: '{uri}' is not an absolute URI.");

            if (!string.IsNullOrEmpty(parsed.Fragment))
                throw new InvalidOperationException(
                    $"{field}: '{uri}' carries a fragment. Browsers never send one to the server, so it can never match.");
        }

        var duplicate = uris.GroupBy(u => u, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"{field}: '{duplicate.Key}' is listed more than once.");
    }

    /// <summary>
    /// Only the three implemented grants. An unrecognised value is not inert: the token endpoint
    /// tests membership of this list, so a typo produces a client that is refused every grant and
    /// reads as correctly configured.
    /// </summary>
    private static void ValidateGrantTypes(OidcApplication entity)
    {
        if (entity.AllowedGrantTypes.Count == 0)
            throw new InvalidOperationException(
                "At least one grant type is required — a client with none can obtain no tokens.");

        foreach (var grant in entity.AllowedGrantTypes)
        {
            if (!SupportedGrantTypes.Contains(grant, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Grant type '{grant}' is not supported. Use one of: {string.Join(", ", SupportedGrantTypes)}.");
        }

        // refresh_token without authorization_code cannot produce a first refresh token, so the
        // combination is unreachable rather than merely unusual.
        if (entity.AllowedGrantTypes.Contains("refresh_token", StringComparer.OrdinalIgnoreCase)
            && !entity.AllowedGrantTypes.Contains("authorization_code", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "refresh_token requires authorization_code — there is no other way for this client to obtain a refresh token.");
        }

        var isConfidential = !string.Equals(entity.ClientType, "public", StringComparison.OrdinalIgnoreCase);
        if (!isConfidential && entity.AllowedGrantTypes.Contains("client_credentials", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A public client cannot use client_credentials — it has no secret to authenticate with.");
        }
    }

    /// <summary>
    /// Accepts a secret typed in cleartext and stores it hashed. The value is never readable
    /// again, so the screen shows the hash and an operator replaces it to rotate.
    /// </summary>
    private static void HashAnyNewSecrets(OidcApplication entity)
    {
        foreach (var secret in entity.Secrets)
        {
            if (string.IsNullOrWhiteSpace(secret.Hash))
                throw new InvalidOperationException("A client secret cannot be empty.");

            if (!ClientSecretHasher.IsHashed(secret.Hash))
                secret.Hash = ClientSecretHasher.Hash(secret.Hash);
        }
    }

    /// <summary>
    /// Rejects a duplicate <c>ClientId</c>. Nothing enforced this, and the lookup that resolves a
    /// client returns whichever document the index yields first — so a second application claiming
    /// an existing id is impersonation decided by ordering.
    /// <para>
    /// Checked after the write rather than before it: a read-then-write check races, since two
    /// concurrent saves both find nothing and both proceed. Reading afterwards catches the loser,
    /// which then fails loudly instead of silently shadowing the original.
    /// </para>
    /// </summary>
    public override async Task<OidcApplication> OnSaveAsync(IAsyncDocumentSession session, PersistentObject obj)
    {
        var entity = await base.OnSaveAsync(session, obj);

        var clash = await session.Query<OidcApplication>()
            .Where(a => a.ClientId == entity.ClientId, exact: true)
            .ToListAsync();

        if (clash.Any(a => !string.Equals(a.Id, entity.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Client id '{entity.ClientId}' is already registered. Client ids must be unique — "
              + "the lookup that resolves them returns whichever document is found first.");
        }

        return entity;
    }
}
