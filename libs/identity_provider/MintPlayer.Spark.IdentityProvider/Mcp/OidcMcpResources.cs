using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// One MCP endpoint agents sign in for here: its canonical URL (the only audience its tokens get), the
/// <c>OidcResources</c> document that defines <c>mcp:read</c>/<c>mcp:write</c> for it, and what the consent screen may
/// offer for it.
/// </summary>
/// <param name="Audience">The endpoint's canonical URL: the token's <c>aud</c> and the RFC 8707 <c>resource</c>.</param>
/// <param name="ResourceId">Its API resource: <c>OidcResources/mcp</c> for this application's endpoint, <c>OidcResources/mcp-{sha256(url)[..32]}</c> for another's.</param>
/// <param name="DisplayName">Another application's name on the consent screen; <see langword="null"/> for this application's own endpoint.</param>
/// <param name="IsLocal">This application's own endpoint (<c>ForMcp()</c>, setup 3): its consent screen offers types and writes the agent grant here.</param>
/// <param name="Permissions">The most a token for it may carry (another application's endpoint only; this application's own asks <see cref="IAgentExposure"/>).</param>
internal sealed record OidcMcpEndpoint(string Audience, string ResourceId, string? DisplayName, bool IsLocal, EAgentPermission Permissions);

/// <summary>
/// The MCP endpoints of the agent server (MCP PRD D5, gaps G4 and G11; remote resources, 2026-10-12): this application's
/// own (<c>ForMcp()</c>, <c>OidcResources/mcp</c>, audience = the MCP canonical URL, D20) and other applications'
/// (<c>ForMcpResource(url)</c>, one resource each, audience = that URL). Every one owns the scopes <c>mcp:read</c> and
/// <c>mcp:write</c>, so a request's <c>resource</c> says which endpoint, and therefore which resource, they belong to.
/// Written at startup.
/// </summary>
/// <remarks>
/// This application's canonical URL comes from the Mcp package through <see cref="IAgentExposure.ResourceUrl"/> (the
/// IdentityProvider does not reference it). It is required: a token whose audience the server would have to guess from a
/// request's <c>Host</c> is the token-passthrough hole the MCP spec forbids, so startup refuses without it. Another
/// application's URL is configured here, and checked at startup.
/// </remarks>
internal sealed class OidcMcpResources(SparkIdentityProviderOptions options)
{
    /// <summary>The endpoints, this application's first. Empty without <c>ForMcp()</c>/<c>ForMcpResource()</c>.</summary>
    public IReadOnlyList<OidcMcpEndpoint> Endpoints { get; private set; } = [];

    /// <summary>Whether agents sign in here for any MCP endpoint.</summary>
    public bool IsEnabled => Endpoints.Count > 0;

    /// <summary>This application's own endpoint (setup 3), or <see langword="null"/>.</summary>
    public OidcMcpEndpoint? Local => Endpoints.FirstOrDefault(e => e.IsLocal);

    /// <summary>The endpoint whose canonical URL is exactly <paramref name="audience"/>.</summary>
    public OidcMcpEndpoint? Find(string? audience)
        => audience is null ? null : Endpoints.FirstOrDefault(e => string.Equals(e.Audience, audience, StringComparison.Ordinal));

    /// <summary>
    /// The endpoint an agent's authorization request, code or refresh token is for: the one its recorded <c>resource</c>
    /// names. <see langword="null"/> when that endpoint is no longer configured: no token, rather than one for an audience
    /// this server stopped serving.
    /// </summary>
    public OidcMcpEndpoint? Of(OidcToken token) => Find(token.Properties.GetValueOrDefault("resource"));

    /// <summary>
    /// The endpoint an agent's request names (D5 G4): exactly one distinct <c>resource</c> that is an endpoint here; with
    /// none named, this application's own endpoint, else the only one there is. <see langword="null"/>, with the reason
    /// for <c>invalid_target</c>, otherwise.
    /// </summary>
    public OidcMcpEndpoint? Select(IReadOnlyList<string> named, out string error)
    {
        error = string.Empty;
        var distinct = named.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
        {
            error = "An agent token is for one MCP endpoint: name one resource.";
            return null;
        }
        if (distinct.Count == 1)
        {
            var named0 = Find(distinct[0]);
            if (named0 is null)
                error = "This resource is not an MCP endpoint agents sign in for here.";
            return named0;
        }

        var implied = Local ?? (Endpoints.Count == 1 ? Endpoints[0] : null);
        if (implied is null)
            error = "Name the MCP endpoint in the resource parameter.";
        return implied;
    }

    /// <summary>Whether a request for <paramref name="scopes"/> is a request for an MCP endpoint.</summary>
    public static bool AsksForMcp(IEnumerable<string> scopes)
        => scopes.Any(s => s is OidcAgentClients.ReadScope or OidcAgentClients.WriteScope);

    /// <summary>
    /// The audience of another application's endpoint as configured: an absolute https URL (http only on a loopback
    /// host, for local development), without query, fragment or user info, and without a trailing <c>/</c> (the Mcp
    /// package drops it from its own canonical URL too). Throws naming the URL otherwise.
    /// </summary>
    internal static string RemoteAudience(string? configured)
    {
        var url = configured?.Trim().TrimEnd('/') ?? string.Empty;
        // The scheme check is load-bearing: on Linux Uri.TryCreate("/x", Absolute) succeeds as file:///x.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || url.Contains('?') || url.Contains('#') || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"idp.ForMcpResource(\"{configured}\"): an MCP endpoint's canonical URL must be an absolute https URL "
                + "(http only on a loopback host) without query, fragment or user info, exactly as that application publishes it.");
        }
        return url;
    }

    /// <summary>The API resource of another application's endpoint: a natural id from its URL, which a document id cannot carry.</summary>
    internal static string RemoteResourceName(string audience)
        => OidcAgentClients.ResourceName + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(audience)))[..32];

    public async Task InitializeAsync(IServiceProvider services, IDocumentStore store)
    {
        if (options.Mcp is not { } mcp)
            return;

        // This hook can run before the migrations (or in a host without them), and it loads OidcResources:
        // bring their texts to the X17 shape first, or the load of an old document fails startup.
        await Migrations.OidcResourceTextUpgrade.RunAsync(store);

        var endpoints = new List<OidcMcpEndpoint>();
        using var session = store.OpenAsyncSession();

        if (mcp.ServesLocalEndpoint)
        {
            using var scope = services.CreateScope();
            var exposure = scope.ServiceProvider.GetService<IAgentExposure>()
                ?? throw new InvalidOperationException(
                    "idp.ForMcp() needs the MCP server: add spark.AddMcp(...) to the application. A provider that signs agents in "
                    + "only for other applications' MCP endpoints lists them with idp.ForMcpResource(url) instead.");
            var audience = exposure.ResourceUrl
                ?? throw new InvalidOperationException(
                    "idp.ForMcp() needs the MCP endpoint's canonical URL, the audience of every agent token: set Spark:Mcp:CanonicalUrl or Spark:Auth:PublicBaseUrl.");

            var id = OidcScopeCatalog.ResourceId(OidcAgentClients.ResourceName);
            var resource = await session.LoadAsync<OidcResource>(id);
            if (resource is null)
            {
                resource = NewResource(id, OidcAgentClients.ResourceName,
                    TranslatedString.Of(("en", "AI agents"), ("fr", "Agents IA"), ("nl", "AI-agents")),
                    TranslatedString.Of(
                        ("en", "What an AI agent may do in this application on your behalf."),
                        ("fr", "Ce qu'un agent IA peut faire dans cette application en votre nom."),
                        ("nl", "Wat een AI-agent namens jou in deze toepassing mag doen.")));
                await session.StoreAsync(resource);
            }
            Complete(resource, audience);
            endpoints.Add(new OidcMcpEndpoint(audience, id, null, IsLocal: true, EAgentPermission.Read | EAgentPermission.Write));
        }

        foreach (var configured in mcp.Resources)
        {
            var audience = RemoteAudience(configured.Url);
            if (endpoints.Any(e => string.Equals(e.Audience, audience, StringComparison.Ordinal)))
                throw new InvalidOperationException($"The MCP endpoint {audience} is listed twice (ForMcp() and ForMcpResource, or ForMcpResource twice).");
            if ((configured.Permissions & EAgentPermission.Read) == 0 || (configured.Permissions & ~(EAgentPermission.Read | EAgentPermission.Write)) != 0)
                throw new InvalidOperationException($"idp.ForMcpResource(\"{audience}\"): Permissions must be Read, or Read and Write.");

            var displayName = string.IsNullOrWhiteSpace(configured.DisplayName) ? new Uri(audience).Authority : configured.DisplayName.Trim();
            var name = RemoteResourceName(audience);
            var id = OidcScopeCatalog.ResourceId(name);
            var resource = await session.LoadAsync<OidcResource>(id);
            if (resource is null)
            {
                resource = NewResource(id, name, TranslatedString.Of(("en", displayName), ("fr", displayName), ("nl", displayName)), TranslatedString.Of(
                    ("en", $"What an AI agent may do in {displayName} on your behalf."),
                    ("fr", $"Ce qu'un agent IA peut faire dans {displayName} en votre nom."),
                    ("nl", $"Wat een AI-agent namens jou in {displayName} mag doen.")));
                await session.StoreAsync(resource);
            }
            Complete(resource, audience);
            endpoints.Add(new OidcMcpEndpoint(audience, id, displayName, IsLocal: false, configured.Permissions));
        }

        if (endpoints.Count == 0)
            return;

        // offline_access: advertised so Claude Code asks for it (D5); agent clients get refresh tokens either way.
        var offline = OidcScopeCatalog.ResourceId(OidcAgentClients.OfflineAccess);
        if (await session.LoadAsync<OidcResource>(offline) is null)
        {
            await session.StoreAsync(new OidcResource
            {
                Id = offline,
                Kind = OidcResourceKinds.Identity,
                Name = OidcAgentClients.OfflineAccess,
                DisplayName = TranslatedString.Of(("en", "Stay signed in"), ("fr", "Rester connecté"), ("nl", "Aangemeld blijven")),
                ShowInDiscoveryDocument = true,
                Enabled = true,
            });
        }

        await session.SaveChangesAsync();
        Endpoints = endpoints;
    }

    private static OidcResource NewResource(string id, string name, TranslatedString displayName, TranslatedString description) => new()
    {
        Id = id,
        Kind = OidcResourceKinds.Api,
        Name = name,
        DisplayName = displayName,
        Description = description,
        Enabled = true,
        // Listed by RFC 8414 (the agents' metadata), not by OpenID discovery, which is for relying parties.
        ShowInDiscoveryDocument = false,
        AutoApprove = true,
        AllowIntrospection = true,
    };

    /// <summary>The audience and the two scopes, on a new resource or one an earlier start wrote (an administrator's switches stay).</summary>
    private static void Complete(OidcResource resource, string audience)
    {
        resource.Audience = audience;
        Ensure(resource, OidcAgentClients.ReadScope, TranslatedString.Of(("en", "Read your data"), ("fr", "Lire vos données"), ("nl", "Je gegevens lezen")));
        Ensure(resource, OidcAgentClients.WriteScope, TranslatedString.Of(("en", "Propose and make changes"), ("fr", "Proposer et effectuer des modifications"), ("nl", "Wijzigingen voorstellen en uitvoeren")));
    }

    private static void Ensure(OidcResource resource, string name, TranslatedString displayName)
    {
        if (!resource.Scopes.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
            resource.Scopes.Add(new OidcApiScope { Name = name, DisplayName = displayName, Enabled = true, ShowInDiscoveryDocument = false });
    }
}
