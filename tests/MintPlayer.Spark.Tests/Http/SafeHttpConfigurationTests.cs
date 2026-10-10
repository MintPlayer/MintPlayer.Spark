using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Http;

namespace MintPlayer.Spark.Tests.Http;

/// <summary>
/// D39 of <c>docs/seo_analyzer_PRD.md</c>: configuration may remove the default ports 80/443. Before, the
/// section was bound with the plain binder, which appends to a non-empty list, so a configured
/// <c>AllowedPorts: [ 443 ]</c> still allowed 80. A list the section names now replaces the list beneath
/// it; a list it does not name, or no section at all, keeps the secure default.
/// </summary>
public class SafeHttpConfigurationTests
{
    private const string Name = "Tests.SafeHttpConfiguration";

    private static IServiceProvider Provider(Dictionary<string, string?> settings, Action<IServiceCollection>? beneath = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        beneath?.Invoke(services);
        services.AddSafeHttpClient(Name, configuration.GetSection("Fetcher"));
        return services.BuildServiceProvider();
    }

    private static SafeHttpOptions Options(IServiceProvider provider)
        => provider.GetRequiredService<IOptionsMonitor<SafeHttpOptions>>().Get(Name);

    [Fact]
    public void Configured_ports_replace_the_default_ports()
    {
        var options = Options(Provider(new() { ["Fetcher:AllowedPorts:0"] = "443" }));

        options.AllowedPorts.Should().Equal(443);
    }

    [Fact]
    public async Task A_port_removed_by_configuration_is_refused()
    {
        var client = Provider(new() { ["Fetcher:AllowedPorts:0"] = "443" })
            .GetRequiredService<IHttpClientFactory>().CreateClient(Name);

        var act = () => client.GetAsync("http://example.com/");

        // Refused before any lookup or connect: the scheme/port check runs first.
        (await act.Should().ThrowAsync<SafeHttpBlockedException>()).Which.Reason.Should().Be(SafeHttpBlockReason.Port);
    }

    [Fact]
    public void Without_configuration_the_secure_defaults_stay()
    {
        var options = Options(Provider(new() { ["Fetcher:MaxRedirects"] = "2" }));

        options.AllowedPorts.Should().Equal(80, 443);
        options.AllowedNetworks.Should().BeEmpty();
        options.BlockedNetworks.Should().BeEmpty();
        options.MaxRedirects.Should().Be(2);
    }

    [Fact]
    public void A_configured_list_replaces_a_code_default_registered_beneath_it()
    {
        var options = Options(Provider(
            new() { ["Fetcher:BlockedNetworks:0"] = "203.0.113.0/24" },
            services => services.Configure<SafeHttpOptions>(Name, o =>
            {
                o.BlockedNetworks.Add("198.51.100.0/24");
                o.AllowedPorts.Add(8443);
            })));

        options.BlockedNetworks.Should().Equal("203.0.113.0/24");
        // Not named by the section: the code default beneath stays.
        options.AllowedPorts.Should().Equal(80, 443, 8443);
    }

    [Fact]
    public void Code_after_the_section_still_adds_to_the_configured_list()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Fetcher:AllowedPorts:0"] = "443" }).Build();
        var services = new ServiceCollection();
        services.AddSafeHttpClient(Name, configuration.GetSection("Fetcher"), o => o.AllowedPorts.Add(8443));

        Options(services.BuildServiceProvider()).AllowedPorts.Should().Equal(443, 8443);
    }
}
