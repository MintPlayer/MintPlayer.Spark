using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MintPlayer.Spark.Authorization.ResourceServer;

/// <summary>
/// The issuers' discovery documents for <see cref="SparkIntrospectionHandler"/>: one <see cref="ConfigurationManager{T}"/>
/// per authority (it caches and refreshes the document), owned by the container rather than the process.
/// </summary>
/// <remarks>
/// Every fetch goes through <see cref="IHttpClientFactory"/>'s <see cref="SparkResourceServerOptions.HttpClientName"/>
/// client, the same one the introspection call uses, so a proxy, a handler or a test stub configured on it applies to
/// both. A client is created per fetch, so the factory's handler rotation (DNS changes) keeps working.
/// </remarks>
internal sealed class SparkResourceServerDiscovery(IHttpClientFactory httpClients)
{
    private readonly ConcurrentDictionary<(string Authority, bool RequireHttps), ConfigurationManager<OpenIdConnectConfiguration>> managers = new();

    public ConfigurationManager<OpenIdConnectConfiguration> For(SparkResourceServerOptions rs)
        => managers.GetOrAdd((rs.Authority.TrimEnd('/'), rs.RequireHttpsMetadata), key =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                key.Authority + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new FactoryDocumentRetriever(httpClients, key.RequireHttps)));

    private sealed class FactoryDocumentRetriever(IHttpClientFactory httpClients, bool requireHttps) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
            => new HttpDocumentRetriever(httpClients.CreateClient(SparkResourceServerOptions.HttpClientName)) { RequireHttps = requireHttps }
                .GetDocumentAsync(address, cancel);
    }
}
