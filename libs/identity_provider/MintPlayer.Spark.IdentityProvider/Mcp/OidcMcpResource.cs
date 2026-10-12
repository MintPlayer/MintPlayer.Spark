using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// The MCP API resource of the embedded authorization server (MCP PRD D5, gaps G4 and G11): <c>OidcResources/mcp</c>,
/// owning <c>mcp:read</c> and <c>mcp:write</c>, whose audience is the MCP canonical URL (D20). Written at startup.
/// </summary>
/// <remarks>
/// The canonical URL comes from the Mcp package through <see cref="IAgentExposure.ResourceUrl"/> (the IdentityProvider
/// does not reference it). It is required: a token whose audience the server would have to guess from a request's
/// <c>Host</c> is the token-passthrough hole the MCP spec forbids, so startup refuses without it.
/// </remarks>
internal sealed class OidcMcpResource
{
    /// <summary>The MCP canonical URL: the only audience an agent token gets, and the only <c>resource</c> it may ask for. <see langword="null"/> without <c>ForMcp()</c>.</summary>
    public string? Audience { get; private set; }

    public bool IsEnabled => Audience is not null;

    /// <summary>Whether a request for <paramref name="scopes"/> is a request for the MCP resource.</summary>
    public static bool AsksForMcp(IEnumerable<string> scopes)
        => scopes.Any(s => s is OidcAgentClients.ReadScope or OidcAgentClients.WriteScope);

    public async Task InitializeAsync(IServiceProvider services, IDocumentStore store)
    {
        using var scope = services.CreateScope();
        var exposure = scope.ServiceProvider.GetService<IAgentExposure>()
            ?? throw new InvalidOperationException("idp.ForMcp() needs the MCP server: add spark.AddMcp(...) to the application.");
        var audience = exposure.ResourceUrl
            ?? throw new InvalidOperationException(
                "idp.ForMcp() needs the MCP endpoint's canonical URL, the audience of every agent token: set Spark:Mcp:CanonicalUrl or Spark:Auth:PublicBaseUrl.");

        using var session = store.OpenAsyncSession();
        var id = OidcScopeCatalog.ResourceId(OidcAgentClients.ResourceName);
        var resource = await session.LoadAsync<OidcResource>(id);
        if (resource is null)
        {
            resource = new OidcResource
            {
                Id = id,
                Kind = OidcResourceKinds.Api,
                Name = OidcAgentClients.ResourceName,
                DisplayName = TranslatedString.Create("AI agents", "Agents IA", "AI-agents"),
                Description = TranslatedString.Create(
                    "What an AI agent may do in this application on your behalf.",
                    "Ce qu'un agent IA peut faire dans cette application en votre nom.",
                    "Wat een AI-agent namens jou in deze toepassing mag doen."),
                Enabled = true,
                // Listed by RFC 8414 (the agents' metadata), not by OpenID discovery, which is for relying parties.
                ShowInDiscoveryDocument = false,
                AutoApprove = true,
                AllowIntrospection = true,
            };
            await session.StoreAsync(resource);
        }

        resource.Audience = audience;
        Ensure(resource, OidcAgentClients.ReadScope, TranslatedString.Create("Read your data", "Lire vos données", "Je gegevens lezen"));
        Ensure(resource, OidcAgentClients.WriteScope, TranslatedString.Create("Propose and make changes", "Proposer et effectuer des modifications", "Wijzigingen voorstellen en uitvoeren"));

        // offline_access: advertised so Claude Code asks for it (D5); agent clients get refresh tokens either way.
        var offline = OidcScopeCatalog.ResourceId(OidcAgentClients.OfflineAccess);
        if (await session.LoadAsync<OidcResource>(offline) is null)
        {
            await session.StoreAsync(new OidcResource
            {
                Id = offline,
                Kind = OidcResourceKinds.Identity,
                Name = OidcAgentClients.OfflineAccess,
                DisplayName = TranslatedString.Create("Stay signed in", "Rester connecté", "Aangemeld blijven"),
                ShowInDiscoveryDocument = true,
                Enabled = true,
            });
        }

        await session.SaveChangesAsync();
        Audience = audience;
    }

    private static void Ensure(OidcResource resource, string name, TranslatedString displayName)
    {
        if (!resource.Scopes.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
            resource.Scopes.Add(new OidcApiScope { Name = name, DisplayName = displayName, Enabled = true, ShowInDiscoveryDocument = false });
    }
}
