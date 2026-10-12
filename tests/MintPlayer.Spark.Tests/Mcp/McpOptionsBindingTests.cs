using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Mcp;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// <c>Spark:Mcp</c> binding (MCP PRD D4, D20, D21): a key the section does not hold leaves its member at the default,
/// although the configuration binder calls every setter. Found wiring Fleet and HR (2026-10-12): an application that
/// configured only <c>Spark:Mcp:CanonicalUrl</c> refused to start unless all three setups were wired.
/// </summary>
public class McpOptionsBindingTests
{
    private static IConfiguration Settings(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build();

    [Fact]
    public void A_canonical_url_from_configuration_leaves_the_setups_at_their_default()
    {
        var options = SparkMcpOptions.Create(Settings(("Spark:Mcp:CanonicalUrl", "https://fleet.example/mcp")), mcp => mcp.ServerName = "Fleet");

        options.CanonicalUrl.Should().Be("https://fleet.example/mcp");
        options.Authentication.Should().Be(EAgentAuthentication.All);
        options.AuthenticationIsExplicit.Should().BeFalse("only a configured or coded value is explicit; the default uses what is wired");
        options.ToolPrefix.Should().Be("fleet", "the prefix still follows the server name the callback set (D21)");
    }

    [Fact]
    public void A_configured_or_coded_setup_is_explicit()
    {
        SparkMcpOptions.Create(Settings(("Spark:Mcp:Authentication", "PersonalAccessToken")), null).AuthenticationIsExplicit.Should().BeTrue();
        SparkMcpOptions.Create(null, mcp => mcp.Authentication = EAgentAuthentication.EmbeddedAuthorizationServer).AuthenticationIsExplicit.Should().BeTrue();
        SparkMcpOptions.Create(Settings(("Spark:Mcp:ToolPrefix", "fl")), mcp => mcp.ServerName = "Fleet").ToolPrefix.Should().Be("fl");
    }
}
