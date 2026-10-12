using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Http;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.IdentityProvider.Mcp;
using MintPlayer.Spark.IdentityProvider.Services;
using EmbeddedHost = MintPlayer.Spark.Tests.Mcp._Infrastructure.McpEmbeddedHost;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The SSRF guards of Client ID Metadata Documents (MCP PRD D5, §6.2 "CIMD to a private address refused"; plan M3b
/// hardening): the fetch is started by an anonymous <c>/connect/authorize</c> with a URL of the caller's choice. The
/// URL rules, the content rules, and the real SSRF-safe client refusing private addresses, private resolutions
/// (DNS rebinding), redirects and oversized answers.
/// </summary>
public class OidcClientMetadataDocumentTests
{
    private const string ClientId = "https://agent.example/oauth/client.json";

    private static (OidcClientMetadataDocuments Documents, SparkIdentityProviderOptions Options) Build(
        Action<IServiceCollection>? services = null, Action<SparkIdentityProviderMcpOptions>? mcp = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        new SparkBuilder(collection, configuration).AddIdentityProvider(o => o.ForMcp(mcp));
        services?.Invoke(collection);
        var provider = collection.BuildServiceProvider();
        var options = provider.GetRequiredService<SparkIdentityProviderOptions>();
        var documents = new OidcClientMetadataDocuments(
            null!, provider.GetRequiredService<IHttpClientFactory>(), options, new OidcAgentRegistrationLimiter(options),
            new OidcAudit(options), NullLogger<OidcClientMetadataDocuments>.Instance);
        return (documents, options);
    }

    // ── the client id URL ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://agent.example/client.json")]            // not https
    [InlineData("https://agent.example")]                       // no path
    [InlineData("https://agent.example/")]                      // no path (and a trailing slash)
    [InlineData("https://agent.example/oauth/")]                // trailing slash
    [InlineData("https://agent.example/client.json#frag")]      // fragment
    [InlineData("https://agent.example/client.json?x=1")]       // query
    [InlineData("https://user:pw@agent.example/client.json")]   // user info
    [InlineData("https://agent.example/a/../client.json")]      // dot segment
    [InlineData("https://agent.example/a/./client.json")]       // dot segment
    [InlineData("https://agent.example/a/%2e%2e/client.json")]  // encoded dot segment
    [InlineData("https://agent.example:8443/client.json")]      // not the default port
    public void A_client_id_URL_outside_the_rules_is_refused(string clientId)
        => Build().Documents.CheckClientId(clientId).Should().NotBeNull();

    [Fact]
    public void A_plain_https_URL_with_a_path_is_accepted()
        => Build().Documents.CheckClientId(ClientId).Should().BeNull();

    [Fact]
    public void A_host_allow_list_refuses_every_other_host()
    {
        var (documents, _) = Build(mcp: m => m.MetadataDocumentHosts = ["claude.ai"]);

        documents.CheckClientId("https://claude.ai/oauth/claude-code-client-metadata").Should().BeNull();
        documents.CheckClientId(ClientId).Should().NotBeNull("an allow-list, when configured, is exhaustive (D5)");
    }

    // ── the document ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_document_that_names_another_client_id_is_refused()
    {
        var (document, error) = OidcClientMetadataDocuments.Parse(ClientId,
            Encoding.UTF8.GetBytes(EmbeddedHost.Document("https://claude.ai/oauth/claude-code-client-metadata")));

        document.Should().BeNull("a document at one URL must not speak for another client");
        error.Should().Contain("client_id");
    }

    [Theory]
    [InlineData("myapp://callback")]
    [InlineData("http://agent.example/callback")]
    [InlineData("https://agent.example/cb#x")]
    public void A_document_with_a_redirect_that_is_neither_https_nor_loopback_is_refused(string redirect)
        => OidcClientMetadataDocuments.Parse(ClientId, Encoding.UTF8.GetBytes(EmbeddedHost.Document(ClientId, "x", redirect)))
            .Document.Should().BeNull();

    [Fact]
    public void A_confidential_client_document_is_refused()
    {
        var json = EmbeddedHost.Document(ClientId).Replace("\"none\"", "\"private_key_jwt\"", StringComparison.Ordinal);
        OidcClientMetadataDocuments.Parse(ClientId, Encoding.UTF8.GetBytes(json)).Document.Should().BeNull();
    }

    [Fact]
    public void A_valid_document_keeps_its_portless_loopback_redirects()
    {
        var (document, error) = OidcClientMetadataDocuments.Parse(ClientId, Encoding.UTF8.GetBytes(EmbeddedHost.Document(ClientId)));

        error.Should().BeNull();
        document!.RedirectUris.Should().Equal("http://localhost/callback", "http://127.0.0.1/callback");
        document.ClientName.Should().Be("Claude Code");
    }

    // ── the fetch, through the real SSRF-safe client ────────────────────────────────────────────────

    [Fact]
    public void The_fetch_client_is_https_only_without_redirects_and_small()
    {
        var options = new ServiceCollection().AddLogging();
        new SparkBuilder(options, new ConfigurationBuilder().Build()).AddIdentityProvider(o => o.ForMcp());
        var safe = options.BuildServiceProvider().GetRequiredService<IOptionsMonitor<SafeHttpOptions>>().Get(OidcClientMetadataDocuments.HttpClientName);

        safe.AllowedPorts.Should().Equal(443);
        safe.FollowRedirects.Should().BeFalse("the document must be at its own URL");
        safe.MaxResponseBytes.Should().Be(5 * 1024);
        safe.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        safe.AllowedNetworks.Should().BeEmpty("no configuration opens private networks for metadata documents");
    }

    [Theory]
    [InlineData("https://10.0.0.1/client.json")]
    [InlineData("https://127.0.0.1/client.json")]
    [InlineData("https://169.254.169.254/latest/meta-data/client.json")]
    [InlineData("https://[::1]/client.json")]
    [InlineData("https://192.168.1.10/client.json")]
    public async Task A_private_address_is_refused(string clientId)
    {
        var (documents, options) = Build();

        var (document, error) = await documents.FetchAsync(clientId, options.Mcp!, CancellationToken.None);

        document.Should().BeNull();
        error.Should().Contain("not a public address");
    }

    [Fact]
    public async Task A_host_name_resolving_to_a_private_address_is_refused()
    {
        // DNS rebinding: a public-looking name whose answer is private. The client resolves once and dials only vetted
        // addresses, so the check and the connection cannot see different answers.
        var (documents, options) = Build(services => services.AddSingleton<ISafeHttpDnsResolver>(new FixedResolver(IPAddress.Parse("10.1.2.3"))));

        var (document, error) = await documents.FetchAsync("https://rebind.example/client.json", options.Mcp!, CancellationToken.None);

        document.Should().BeNull();
        error.Should().Contain("not a public address");
    }

    [Fact]
    public async Task An_oversized_document_is_refused()
    {
        var big = "{\"client_id\":\"" + ClientId + "\",\"pad\":\"" + new string('x', 6 * 1024) + "\"}";
        var (documents, options) = Build(services => services.AddHttpClient(OidcClientMetadataDocuments.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new EmbeddedHost.DocumentServer().Serve(ClientId, big)));

        var (document, error) = await documents.FetchAsync(ClientId, options.Mcp!, CancellationToken.None);

        document.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        var (documents, options) = Build(services => services.AddHttpClient(OidcClientMetadataDocuments.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new EmbeddedHost.DocumentServer().Serve(ClientId, () =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://10.0.0.1/client.json");
                return redirect;
            })));

        var (document, error) = await documents.FetchAsync(ClientId, options.Mcp!, CancellationToken.None);

        document.Should().BeNull("a redirect, to a private address or anywhere, is never followed");
        error.Should().Contain("could not be fetched");
    }

    private sealed class FixedResolver(IPAddress address) : ISafeHttpDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(new[] { address });
    }
}
