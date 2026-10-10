using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Http;
using MintPlayer.Spark.IdentityProvider.Extensions;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// M1 of <c>docs/seo_analyzer_PRD.md</c> (X2): the provider fetches a client's <c>jwks_uri</c> and posts to its
/// <c>backchannel_logout_uri</c>, both URIs of the client's choosing, through the SSRF-safe client. Before,
/// both used a plain named client, so a registered URI could point the provider at loopback, the private
/// network or the cloud metadata address.
/// </summary>
public class OidcClientRequestsSafeHttpTests
{
    private static IServiceProvider Provider(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new SparkBuilder(services, configuration).AddIdentityProvider();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(SparkIdentityProviderExtensions.JwksHttpClient, "https://127.0.0.1/jwks")]
    [InlineData(SparkIdentityProviderExtensions.JwksHttpClient, "https://169.254.169.254/latest/meta-data/")]
    [InlineData(SparkIdentityProviderExtensions.JwksHttpClient, "https://[::1]/jwks")]
    [InlineData(SparkIdentityProviderExtensions.BackChannelLogoutHttpClient, "https://10.0.0.1/logout")]
    [InlineData(SparkIdentityProviderExtensions.BackChannelLogoutHttpClient, "http://192.168.1.1/logout")]
    public async Task A_client_uri_on_an_internal_address_is_refused(string client, string uri)
    {
        var http = Provider().GetRequiredService<IHttpClientFactory>().CreateClient(client);

        var act = () => http.GetAsync(uri);

        (await act.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.Address);
    }

    [Fact]
    public void The_two_clients_have_their_own_limits()
    {
        var options = Provider().GetRequiredService<IOptionsMonitor<SafeHttpOptions>>();

        var jwks = options.Get(SparkIdentityProviderExtensions.JwksHttpClient);
        jwks.Timeout.Should().Be(TimeSpan.FromSeconds(10));
        jwks.MaxRedirects.Should().Be(5);
        jwks.MaxResponseBytes.Should().Be(1024 * 1024);

        var logout = options.Get(SparkIdentityProviderExtensions.BackChannelLogoutHttpClient);
        logout.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        logout.MaxRedirects.Should().Be(0);
        logout.AllowedNetworks.Should().BeEmpty();
    }

    [Fact]
    public void Loopback_for_a_local_relying_party_is_opened_only_by_configuration()
    {
        var options = Provider(new()
        {
            ["Spark:IdentityProvider:ClientRequests:AllowedNetworks:0"] = "127.0.0.1/32",
            ["Spark:IdentityProvider:ClientRequests:AllowedPorts:0"] = "5001",
        }).GetRequiredService<IOptionsMonitor<SafeHttpOptions>>();

        foreach (var name in new[] { SparkIdentityProviderExtensions.JwksHttpClient, SparkIdentityProviderExtensions.BackChannelLogoutHttpClient })
        {
            var o = options.Get(name);
            o.AllowedNetworks.Should().Equal("127.0.0.1/32");
            o.AllowedPorts.Should().Contain(5001).And.Contain(443).And.Contain(80);
        }
        // The section adds to the defaults; it does not undo the per-client limits.
        options.Get(SparkIdentityProviderExtensions.BackChannelLogoutHttpClient).MaxRedirects.Should().Be(0);
    }
}
