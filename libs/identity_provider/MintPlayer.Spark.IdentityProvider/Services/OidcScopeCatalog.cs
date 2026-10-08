using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Resolves scope names to their definitions (<see cref="OidcScopeDefinition"/>), flattened from the
/// <see cref="OidcResource"/> that defines each (<c>docs/identity_provider_platform_PRD.md</c> D1).
/// <para>
/// By point-load, not by query: an identity scope <c>email</c> lives at <c>OidcResources/email</c>,
/// and an API scope <c>fleet.read</c> in the resource named by its prefix, <c>OidcResources/fleet</c>.
/// A load is strongly consistent, so a scope disabled a moment ago is never read back as enabled
/// from a stale index, which is what the query this replaced could do.
/// </para>
/// </summary>
public static class OidcScopeCatalog
{
    private const string CollectionPrefix = "OidcResources/";

    /// <summary>The document id of the resource named <paramref name="resourceName"/>.</summary>
    public static string ResourceId(string resourceName) => CollectionPrefix + resourceName;

    /// <summary>The resource name an API scope belongs to: the part before its first dot, or null.</summary>
    public static string? ApiResourceNameOf(string scopeName)
    {
        var dot = scopeName.IndexOf('.');
        return dot > 0 ? scopeName[..dot] : null;
    }

    /// <summary>
    /// The enabled definitions of <paramref name="scopeNames"/>, in their order. A name that is not
    /// defined, or whose scope or resource is disabled, is left out.
    /// </summary>
    public static async Task<List<OidcScopeDefinition>> LoadAsync(
        IAsyncDocumentSession session, IEnumerable<string> scopeNames, CancellationToken ct)
    {
        var names = scopeNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
            return [];

        var ids = names
            .SelectMany(n => ApiResourceNameOf(n) is { } api ? new[] { ResourceId(n), ResourceId(api) } : [ResourceId(n)])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var resources = await session.LoadAsync<OidcResource>(ids, ct);

        var result = new List<OidcScopeDefinition>(names.Count);
        foreach (var name in names)
        {
            if (Resolve(resources, name) is { } definition)
                result.Add(definition);
        }
        return result;
    }

    /// <summary>Every enabled scope listed in the discovery document.</summary>
    public static async Task<List<OidcScopeDefinition>> LoadDiscoverableAsync(IAsyncDocumentSession session, CancellationToken ct)
    {
        // A query, not loads: discovery lists what exists, and a few seconds of index staleness on a
        // public listing decides nothing. Every decision path above loads.
        var resources = await session.Query<OidcResource>()
            .Where(r => r.Enabled && r.ShowInDiscoveryDocument)
            .ToListAsync(ct);

        return resources
            .SelectMany(Flatten)
            .Where(d => d.ShowInDiscoveryDocument)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every enabled scope a resource defines.</summary>
    public static IEnumerable<OidcScopeDefinition> Flatten(OidcResource resource)
    {
        if (!resource.Enabled)
            yield break;

        if (resource.Kind == OidcResourceKinds.Api)
        {
            foreach (var scope in resource.Scopes.Where(s => s.Enabled))
                yield return Define(resource, scope);
        }
        else
        {
            yield return Define(resource);
        }
    }

    private static OidcScopeDefinition? Resolve(Dictionary<string, OidcResource> resources, string name)
    {
        if (resources.TryGetValue(ResourceId(name), out var identity)
            && identity is { Kind: OidcResourceKinds.Identity, Enabled: true }
            && string.Equals(identity.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return Define(identity);
        }

        if (ApiResourceNameOf(name) is { } apiName
            && resources.TryGetValue(ResourceId(apiName), out var api)
            && api is { Kind: OidcResourceKinds.Api, Enabled: true }
            && api.Scopes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { Enabled: true } scope)
        {
            return Define(api, scope);
        }

        return null;
    }

    private static OidcScopeDefinition Define(OidcResource identity) => new(
        identity.Name, identity.Name, OidcResourceKinds.Identity, identity.DisplayName, identity.Description,
        identity.ClaimTypes, identity.Required, identity.Emphasize, identity.ShowInDiscoveryDocument);

    private static OidcScopeDefinition Define(OidcResource api, OidcApiScope scope) => new(
        scope.Name, api.Name, OidcResourceKinds.Api, scope.DisplayName ?? api.DisplayName, scope.Description,
        scope.ClaimTypes, false, scope.Emphasize, scope.ShowInDiscoveryDocument && api.ShowInDiscoveryDocument);
}
