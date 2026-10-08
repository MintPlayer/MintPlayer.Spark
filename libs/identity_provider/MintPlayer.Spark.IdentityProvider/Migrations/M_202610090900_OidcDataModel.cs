using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Migrations;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Migrations;

/// <summary>
/// Moves the identity provider's documents into the platform data model
/// (<c>docs/identity_provider_platform_PRD.md</c> D1, I1):
/// <list type="bullet">
/// <item><c>OidcScopes</c> → <c>OidcResources</c>. A scope without audiences becomes an identity
/// resource. A scope with audiences becomes a scope of the API resource named by each audience; a
/// scope name without that API's prefix gets it (<c>read</c> on <c>fleet</c> becomes <c>fleet.read</c>),
/// and the applications' scope lists are renamed with it.</item>
/// <item><c>OidcApplications.AllowedScopes</c> → <c>Scopes[]</c> (required when the old scope was;
/// approved, since they were in use), in <c>Live</c> mode (they were serving users), secrets given ids.</item>
/// <item><c>OidcAuthorizations</c> → <c>OidcGrants</c>, same natural id under the new prefix, and every
/// token's <c>AuthorizationId</c> with it.</item>
/// <item><c>OidcAuthorizationRequests</c> are deleted: they live ten minutes and are re-created by the
/// next sign-in.</item>
/// <item>Every <c>OidcTokens</c> document gets <c>@expires</c>, replacing the hourly cleanup job (O13).</item>
/// </list>
/// Idempotent: each step reads only the old shape, and a second run finds none.
/// Measured before writing it (R3): no production database holds identity-provider documents.
/// </summary>
public partial class M_202610090900_OidcDataModel : ISparkMigration
{
    public static long Version => 202610090900;
    public static string? Description => "Identity provider platform data model: OidcResources, OidcGrants, application Scopes[], token expiry";

    [Inject] private readonly IDocumentStore store;

    private sealed class LegacyScope
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public List<string> ClaimTypes { get; set; } = [];
        public List<string> Audiences { get; set; } = [];
        public bool Required { get; set; }
        public bool Emphasize { get; set; }
        public bool ShowInDiscoveryDocument { get; set; } = true;
        public bool Enabled { get; set; } = true;
    }

    private sealed class LegacyApplicationScopes
    {
        public string? Id { get; set; }
        public List<string>? AllowedScopes { get; set; }
    }

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var renames = await MigrateScopesAsync(cancellationToken);
        await MigrateApplicationsAsync(renames, cancellationToken);
        await MigrateGrantsAsync(cancellationToken);
        await RunAsync("from OidcAuthorizationRequests as r update { del(id(r)); }", cancellationToken);
        // ExpiresAt is stored as an ISO string, which is what @expires reads.
        await RunAsync(
            "from OidcTokens as t update { "
          + "if (t.AuthorizationId && t.AuthorizationId.startsWith('OidcAuthorizations/')) { t.AuthorizationId = 'OidcGrants/' + t.AuthorizationId.substring(19); } "
          + "if (!t['@metadata']['@expires'] && t.ExpiresAt) { t['@metadata']['@expires'] = t.ExpiresAt; } }",
            cancellationToken);
    }

    /// <summary>Returns old scope name → new scope name, for the names that changed.</summary>
    private async Task<Dictionary<string, string>> MigrateScopesAsync(CancellationToken ct)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var session = store.OpenAsyncSession();
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;

        var legacy = await session.Advanced.AsyncRawQuery<LegacyScope>("from OidcScopes").ToListAsync(ct);
        if (legacy.Count == 0)
            return renames;

        var resources = new Dictionary<string, OidcResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in legacy)
        {
            if (scope.Audiences.Count == 0)
            {
                var identity = await LoadOrNewAsync(session, resources, scope.Name, OidcResourceKinds.Identity, ct);
                identity.DisplayName ??= Translated(scope.DisplayName);
                identity.Description ??= Translated(scope.Description);
                identity.ClaimTypes = scope.ClaimTypes;
                identity.Required = scope.Required;
                identity.Emphasize = scope.Emphasize;
                identity.ShowInDiscoveryDocument = scope.ShowInDiscoveryDocument;
                identity.Enabled = scope.Enabled;
                continue;
            }

            foreach (var audience in scope.Audiences.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                var api = await LoadOrNewAsync(session, resources, audience, OidcResourceKinds.Api, ct);
                var name = scope.Name.StartsWith(audience + ".", StringComparison.Ordinal) ? scope.Name : $"{audience}.{scope.Name}";
                if (!string.Equals(name, scope.Name, StringComparison.Ordinal))
                    renames[scope.Name] = name;
                if (api.Scopes.All(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    api.Scopes.Add(new OidcApiScope
                    {
                        Name = name,
                        DisplayName = Translated(scope.DisplayName),
                        Description = Translated(scope.Description),
                        ClaimTypes = scope.ClaimTypes,
                        Emphasize = scope.Emphasize,
                        Enabled = scope.Enabled,
                        ShowInDiscoveryDocument = scope.ShowInDiscoveryDocument,
                    });
                }
            }
        }

        foreach (var scope in legacy)
            session.Delete(scope.Id!);

        await session.SaveChangesAsync(ct);
        return renames;
    }

    private static async Task<OidcResource> LoadOrNewAsync(
        IAsyncDocumentSession session, Dictionary<string, OidcResource> resources, string name, string kind, CancellationToken ct)
    {
        if (resources.TryGetValue(name, out var known))
            return known;

        var id = OidcScopeCatalog.ResourceId(name);
        var resource = await session.LoadAsync<OidcResource>(id, ct);
        if (resource is null)
        {
            resource = new OidcResource { Id = id, Name = name, Kind = kind };
            await session.StoreAsync(resource, ct);
        }
        resources[name] = resource;
        return resource;
    }

    private async Task MigrateApplicationsAsync(Dictionary<string, string> renames, CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.MaxNumberOfRequestsPerSession = int.MaxValue;

        var legacy = await session.Advanced
            .AsyncRawQuery<LegacyApplicationScopes>("from OidcApplications where exists(AllowedScopes)")
            .ToListAsync(ct);
        if (legacy.Count == 0)
            return;

        var definitions = await OidcScopeCatalog.LoadAsync(
            session, legacy.SelectMany(l => l.AllowedScopes ?? []).Select(n => renames.GetValueOrDefault(n, n)), ct);

        foreach (var old in legacy)
        {
            var app = await session.LoadAsync<OidcApplication>(old.Id!, ct);
            foreach (var name in (old.AllowedScopes ?? []).Select(n => renames.GetValueOrDefault(n, n)))
            {
                if (app.Scopes.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                app.Scopes.Add(new OidcApplicationScope
                {
                    Name = name,
                    Required = definitions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.Required == true,
                    Status = OidcScopeStatuses.Approved,
                });
            }

            app.Mode = OidcApplicationModes.Live;
            foreach (var secret in app.Secrets.Where(s => string.IsNullOrEmpty(s.SecretId)))
                secret.SecretId = Guid.NewGuid().ToString("N")[..12];
        }

        await session.SaveChangesAsync(ct);

        // The old field is dropped from the documents: RavenDB would otherwise keep it for ever
        // (PreserveDocumentPropertiesNotFoundOnModel), and the query above would find it again.
        await RunAsync("from OidcApplications as a where exists(AllowedScopes) update { delete a.AllowedScopes; }", ct);
    }

    private async Task MigrateGrantsAsync(CancellationToken ct)
    {
        // Same natural id under the new prefix (OidcGrantReference hashes subject|application), so
        // tokens and the user's connected-applications page keep pointing at the right grant.
        await RunAsync(
            "from OidcAuthorizations as a update { "
          + "var copy = JSON.parse(JSON.stringify(a)); "
          + "copy['@metadata'] = { '@collection': 'OidcGrants', 'Raven-Clr-Type': $clr }; "
          + "put('OidcGrants/' + id(a).substring(19), copy); del(id(a)); }",
            ct,
            new Parameters { ["clr"] = store.Conventions.FindClrTypeName(typeof(OidcGrant)) });
    }

    private async Task RunAsync(string rql, CancellationToken ct, Parameters? parameters = null)
    {
        // The collections are constants of this migration; nothing from a request reaches this RQL.
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery { Query = rql, QueryParameters = parameters ?? [] },
                new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: ct);
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }

    private static TranslatedString? Translated(string? english)
        => string.IsNullOrWhiteSpace(english) ? null : TranslatedString.Create(english);
}
