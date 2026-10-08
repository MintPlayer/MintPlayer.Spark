using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Interceptors;

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
public sealed partial class OidcApplicationInterceptors : IBeforeSave<OidcApplication>, IAfterSave<OidcApplication>, IBeforeDelete<OidcApplication>, IAfterDelete<OidcApplication>
{
    private static readonly string[] SupportedGrantTypes =
        ["authorization_code", "refresh_token", "client_credentials"];

    /// <summary>
    /// Always registered, even when <c>EnableDynamicCors</c> is off — an unused snapshot costs
    /// nothing, and an optional dependency here would have to be an optional constructor parameter,
    /// which the injection generator cannot place before the required ones.
    /// </summary>
    [Inject] private readonly OidcCorsOrigins corsOrigins;
    [Inject] private readonly MintPlayer.Spark.Abstractions.Authorization.IAccessControl accessControl;
    [Inject] private readonly Configuration.SparkIdentityProviderOptions options;

    public async ValueTask OnBeforeSaveAsync(OidcApplication entity, SaveContext context)
    {
        StampNewApplication(entity, context);

        if (string.IsNullOrWhiteSpace(entity.ClientId))
            throw new SparkValidationException("Client id is required.", nameof(entity.ClientId));

        // D5: generated server-side and read-only. Every token, grant and redirect a client ever
        // obtained is tied to its client id, so changing it orphans all of them.
        if (context.Before is OidcApplication before
            && !string.IsNullOrEmpty(before.ClientId)
            && !string.Equals(before.ClientId, entity.ClientId, StringComparison.Ordinal))
        {
            throw new SparkValidationException("The client id cannot be changed.", nameof(entity.ClientId));
        }

        ValidateRedirectUris(entity.RedirectUris, nameof(entity.RedirectUris));
        ValidateRedirectUris(entity.PostLogoutRedirectUris, nameof(entity.PostLogoutRedirectUris));
        ValidateRedirectSchemes(entity, entity.RedirectUris, nameof(entity.RedirectUris));
        ValidateRedirectSchemes(entity, entity.PostLogoutRedirectUris, nameof(entity.PostLogoutRedirectUris));
        if (entity.RedirectUris.Count + entity.PostLogoutRedirectUris.Count > options.Apps.MaxRedirectUris)
            throw new SparkValidationException(
                $"An application can register at most {options.Apps.MaxRedirectUris} redirect URIs.", nameof(entity.RedirectUris));
        ValidateCorsOrigins(entity.AllowedCorsOrigins, nameof(entity.AllowedCorsOrigins));
        ValidateGrantTypes(entity);
        ValidateScopes(entity);
        ValidateMode(entity);
        HashAnyNewSecrets(entity);

        // Not a session only when the interceptor is called by hand (unit tests of the rules above).
        if (context.Session is IAsyncDocumentSession session)
        {
            await ApplyScopeApprovalsAsync(session, entity, context);
            // Before the commit (#467 finding, #482): checked only afterwards, a duplicate was refused
            // with a 400 while staying stored.
            await EnsureClientIdUniqueAsync(session, entity);
            // The query above races two concurrent saves: both find nothing and both proceed. The
            // compare-exchange reservation is the atomic half (O17).
            await OidcClientIdReservation.ReserveAsync(session, entity);
        }
    }

    /// <summary>
    /// A new application gets a generated client id, a document id of its own (the reservation
    /// names it), Development mode (D4), and its creator as its first Admin (D3).
    /// </summary>
    private static void StampNewApplication(OidcApplication entity, SaveContext context)
    {
        if (!context.IsNew)
            return;

        if (string.IsNullOrWhiteSpace(entity.ClientId))
            entity.ClientId = OidcClientIdReservation.GenerateClientId();

        entity.Id ??= "OidcApplications/" + Guid.NewGuid().ToString("N");
        entity.CreatedAt ??= DateTime.UtcNow;

        if (context.User?.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId)
        {
            entity.CreatedBy ??= userId;
            if (entity.Members.Count == 0)
            {
                entity.Members.Add(new OidcApplicationMember
                {
                    MemberId = Guid.NewGuid().ToString("N"),
                    UserId = userId,
                    Role = OidcMemberRoles.Admin,
                    Status = OidcMemberStatuses.Active,
                    AcceptedAt = DateTime.UtcNow,
                });
            }
        }
    }

    private static void ValidateScopes(OidcApplication entity)
    {
        foreach (var scope in entity.Scopes)
        {
            scope.Name = scope.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(scope.Name) || scope.Name.Any(char.IsWhiteSpace))
                throw new SparkValidationException(
                    $"'{scope.Name}' is not a scope name. Scopes are space-delimited on the wire, so a name has no whitespace.",
                    nameof(entity.Scopes));

            if (scope.Status is not (OidcScopeStatuses.Approved or OidcScopeStatuses.Pending or OidcScopeStatuses.Rejected))
                throw new SparkValidationException($"Scope '{scope.Name}' has an unknown status '{scope.Status}'.", nameof(entity.Scopes));
        }

        var duplicate = entity.Scopes.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new SparkValidationException($"Scope '{duplicate.Key}' is listed more than once.", nameof(entity.Scopes));
    }

    /// <summary>
    /// D4: an API scope from a resource none of the application's members owns starts as
    /// <c>Pending</c> until an owner (or an identity-provider administrator) approves it. Identity
    /// scopes, the team's own APIs and <c>AutoApprove</c> resources are approved at once. A scope the
    /// application already had keeps its status: editing the application never re-opens a decision.
    /// An administrator adding a scope is the approval.
    /// </summary>
    private async Task ApplyScopeApprovalsAsync(IAsyncDocumentSession session, OidcApplication entity, SaveContext context)
    {
        var before = (context.Before as OidcApplication)?.Scopes ?? [];
        var added = entity.Scopes
            .Where(s => !before.Any(b => string.Equals(b.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (added.Count == 0)
            return;

        // Statuses are the server's to decide: a value posted for a new scope means nothing.
        foreach (var scope in added)
            scope.Status = OidcScopeStatuses.Approved;

        if (context.IsSystemContext || await accessControl.IsAllowedAsync(Actions.OidcApplicationActions.ManageAllResource))
            return;

        var team = entity.Members.Where(m => m.Status == OidcMemberStatuses.Active && m.UserId is not null)
            .Select(m => m.UserId!).ToHashSet(StringComparer.Ordinal);
        foreach (var scope in added)
        {
            if (OidcScopeCatalog.ApiResourceNameOf(scope.Name) is not { } apiName)
                continue; // identity scope
            var api = await session.LoadAsync<OidcResource>(OidcScopeCatalog.ResourceId(apiName));
            if (api is null || api.Kind != OidcResourceKinds.Api || api.AutoApprove || api.Owners.Any(team.Contains))
                continue;
            scope.Status = OidcScopeStatuses.Pending;
            scope.Review = new OidcReviewDecision
            {
                Status = OidcScopeStatuses.Pending,
                RequestedAt = DateTime.UtcNow,
                RequestedBy = context.User?.FindFirstValue(ClaimTypes.NameIdentifier),
            };
        }
    }

    private static void ValidateMode(OidcApplication entity)
    {
        if (entity.Mode is not (OidcApplicationModes.Development or OidcApplicationModes.Live))
            throw new SparkValidationException(
                $"Mode must be '{OidcApplicationModes.Development}' or '{OidcApplicationModes.Live}'.", nameof(entity.Mode));
    }

    private static async Task EnsureClientIdUniqueAsync(IAsyncDocumentSession session, OidcApplication entity)
    {
        var clash = await session.Query<OidcApplication>()
            .Where(a => a.ClientId == entity.ClientId, exact: true)
            .ToListAsync();

        if (clash.Any(a => !string.Equals(a.Id, entity.Id, StringComparison.Ordinal)))
        {
            throw new SparkValidationException(
                $"Client id '{entity.ClientId}' is already registered. Client ids must be unique — "
              + "the lookup that resolves them returns whichever document is found first.",
                nameof(entity.ClientId));
        }
    }

    /// <summary>
    /// A redirect URI is compared verbatim at authorize time, so anything that would not match
    /// exactly is a client that can never complete a flow.
    /// <para>
    /// <c>Uri.TryCreate(..., UriKind.Absolute, ...)</c> alone is <b>not</b> an absoluteness test,
    /// and the difference is platform-dependent: on Unix a bare path like <c>/callback</c> parses
    /// successfully as <c>file:///callback</c>, while on Windows it fails. So this validation
    /// passed on a developer's machine and <b>failed open on Linux</b> — which is where CI runs
    /// and where the app is deployed. Requiring the string to declare the scheme the parser
    /// reports closes it on every platform, and keeps custom schemes
    /// (<c>com.example.app:/cb</c>) working for native clients.
    /// </para>
    /// </summary>
    /// <summary>
    /// D5: https, or http on a loopback address only (a local dev server, RFC 8252 §7.3). A private-use
    /// scheme (<c>com.example.app:/cb</c>, RFC 8252 §7.1) is for native apps, which are public clients.
    /// A confidential web client redirecting over plain http would send its code across the network in clear.
    /// </summary>
    private static void ValidateRedirectSchemes(OidcApplication entity, List<string> uris, string field)
    {
        foreach (var uri in uris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                continue; // ValidateRedirectUris already refused it
            if (parsed.Scheme == Uri.UriSchemeHttps)
                continue;
            if (parsed.Scheme == Uri.UriSchemeHttp)
            {
                if (parsed.IsLoopback)
                    continue;
                throw new SparkValidationException($"'{uri}' uses http. Only a loopback address (localhost, 127.0.0.1, [::1]) may; anything else needs https.", field);
            }
            if (!string.Equals(entity.ClientType, "public", StringComparison.OrdinalIgnoreCase))
                throw new SparkValidationException($"'{uri}' uses the scheme '{parsed.Scheme}'. A private-use scheme is for a native app, which is a public client.", field);
        }
    }

    private static void ValidateRedirectUris(List<string> uris, string field)
    {
        foreach (var uri in uris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                || !uri.StartsWith(parsed.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                throw new SparkValidationException($"'{uri}' is not an absolute URI.", field);
            }

            // Only reachable when the operator typed the scheme, since the check above now rejects
            // the path that silently acquired it. A browser will not navigate to a local file from
            // an HTTPS page anyway, so a client registered this way could never complete a flow.
            if (parsed.IsFile)
                throw new SparkValidationException(
                    $"'{uri}' is a file URI. A redirect target has to be somewhere a browser can be sent.", field);

            if (!string.IsNullOrEmpty(parsed.Fragment))
                throw new SparkValidationException(
                    $"'{uri}' carries a fragment. Browsers never send one to the server, so it can never match.", field);
        }

        var duplicate = uris.GroupBy(u => u, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new SparkValidationException($"'{duplicate.Key}' is listed more than once.", field);
    }

    /// <summary>
    /// A CORS origin is compared byte-for-byte against the browser's <c>Origin</c> header, so it has
    /// to be exactly scheme + host + optional port.
    /// <para>
    /// ⚠️ A trailing slash is <b>normalised away</b> rather than refused: it has one unambiguous
    /// reading and it is what an operator most often types. Anything with real content beyond the
    /// origin — a path, a query, credentials — is refused, because as configured it could never
    /// match and nothing would say so: CORS would silently not work for that one application while
    /// the screen showed the origin listed. Same class of failure as the redirect-URI fragment
    /// above, and refused for the same reason.
    /// </para>
    /// </summary>
    private static void ValidateCorsOrigins(List<string> origins, string field)
    {
        foreach (var origin in origins)
        {
            if (OidcCorsOrigins.Normalize(origin) is null)
                throw new SparkValidationException(
                    $"'{origin}' is not a browser origin. It has to be scheme + host + optional port, "
                    + "with no path, query, fragment or credentials — e.g. https://app.example.com.",
                    field);
        }

        var duplicate = origins.GroupBy(o => o, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new SparkValidationException($"'{duplicate.Key}' is listed more than once.", field);
    }

    /// <summary>
    /// Only the three implemented grants. An unrecognised value is not inert: the token endpoint
    /// tests membership of this list, so a typo produces a client that is refused every grant and
    /// reads as correctly configured.
    /// </summary>
    private static void ValidateGrantTypes(OidcApplication entity)
    {
        if (entity.AllowedGrantTypes.Count == 0)
            throw new SparkValidationException(
                "At least one grant type is required — a client with none can obtain no tokens.",
                nameof(entity.AllowedGrantTypes));

        foreach (var grant in entity.AllowedGrantTypes)
        {
            if (!SupportedGrantTypes.Contains(grant, StringComparer.OrdinalIgnoreCase))
                throw new SparkValidationException(
                    $"Grant type '{grant}' is not supported. Use one of: {string.Join(", ", SupportedGrantTypes)}.",
                    nameof(entity.AllowedGrantTypes));
        }

        // refresh_token without authorization_code cannot produce a first refresh token, so the
        // combination is unreachable rather than merely unusual.
        if (entity.AllowedGrantTypes.Contains("refresh_token", StringComparer.OrdinalIgnoreCase)
            && !entity.AllowedGrantTypes.Contains("authorization_code", StringComparer.OrdinalIgnoreCase))
        {
            throw new SparkValidationException(
                "refresh_token requires authorization_code — there is no other way for this client to obtain a refresh token.",
                nameof(entity.AllowedGrantTypes));
        }

        var isConfidential = !string.Equals(entity.ClientType, "public", StringComparison.OrdinalIgnoreCase);
        if (!isConfidential && entity.AllowedGrantTypes.Contains("client_credentials", StringComparer.OrdinalIgnoreCase))
        {
            throw new SparkValidationException(
                "A public client cannot use client_credentials — it has no secret to authenticate with.",
                nameof(entity.AllowedGrantTypes));
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
                throw new SparkValidationException("A client secret cannot be empty.", nameof(entity.Secrets));

            if (!ClientSecretHasher.IsHashed(secret.Hash))
                secret.Hash = ClientSecretHasher.Hash(secret.Hash);

            // Revocation and the audit trail name a secret by this id, never by its value (D5).
            if (string.IsNullOrEmpty(secret.SecretId))
                secret.SecretId = Guid.NewGuid().ToString("N")[..12];
        }
    }

    /// <summary>
    /// The CORS snapshot is cached with a TTL backstop, so without this an operator adding an origin
    /// would watch the screen say it saved and the browser keep refusing for minutes. The admin screen
    /// is the only in-app writer, which is what makes invalidating after its commit enough.
    /// </summary>
    public ValueTask OnAfterSaveAsync(OidcApplication entity, SaveContext context)
    {
        corsOrigins.Invalidate();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// D3: deleting is the application Admin's, not a Developer's, although both pass the
    /// members-only row filter. An identity-provider administrator may delete any application.
    /// </summary>
    public async ValueTask OnBeforeDeleteAsync(OidcApplication entity, DeleteContext context)
    {
        if (context.IsSystemContext || await accessControl.IsAllowedAsync(Actions.OidcApplicationActions.ManageAllResource))
            return;

        var userId = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (entity.ActiveMember(userId) is not { Role: OidcMemberRoles.Admin })
            throw new SparkValidationException("Only an Admin of this application can delete it.", nameof(entity.Members));
    }

    /// <summary>Frees the client id of a deleted application, and its CORS origins.</summary>
    public async ValueTask OnAfterDeleteAsync(OidcApplication entity, DeleteContext context)
    {
        corsOrigins.Invalidate();
        if (context.Session is IAsyncDocumentSession session && !string.IsNullOrEmpty(entity.ClientId))
            await OidcClientIdReservation.ReleaseAsync(session, entity);
    }
}
