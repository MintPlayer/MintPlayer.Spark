using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// The one public-base-URL rule (#268 M6 dedupe): it replaced four copies (account mails, the IdP portal,
/// organization invitations, and now notifications and share links). The behaviour each copy had is pinned here.
/// </summary>
public class SparkPublicBaseUrlTests
{
    private static HttpContext Request(string host = "evil.example", string scheme = "https", string pathBase = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.PathBase = pathBase;
        return context;
    }

    private static IConfiguration Config(string? value)
        => new ConfigurationBuilder().AddInMemoryCollection(value is null ? [] : [new("Spark:Auth:PublicBaseUrl", value)]).Build();

    [Fact]
    public void An_options_value_wins_over_configuration_and_loses_its_trailing_slash()
        => SparkPublicBaseUrl.Resolve("https://app.example/", Config("https://other.example"), Env("Production"), Request()).Should().Be("https://app.example");

    [Fact]
    public void Configuration_applies_when_the_options_value_is_unset()
        => SparkPublicBaseUrl.Resolve(null, Config("https://app.example/"), Env("Production"), Request()).Should().Be("https://app.example");

    [Fact]
    public void A_blank_value_counts_as_unset()
        => SparkPublicBaseUrl.Resolve("  ", Config(null), Env("Development"), Request("localhost:5001")).Should().Be("https://localhost:5001");

    [Fact]
    public void Development_falls_back_to_the_requests_origin_with_its_path_base()
        => SparkPublicBaseUrl.Resolve(null, Config(null), Env("Development"), Request("localhost:5001", "https", "/app")).Should().Be("https://localhost:5001/app");

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Outside_Development_the_Host_header_is_never_used(string environment)
        => SparkPublicBaseUrl.Resolve(null, Config(null), Env(environment), Request()).Should().BeNull();

    [Fact]
    public void No_request_and_nothing_configured_is_none_even_in_Development()
        => SparkPublicBaseUrl.Resolve(null, Config(null), Env("Development"), null).Should().BeNull();

    private static IHostEnvironment Env(string name) => new TestEnvironment { EnvironmentName = name };

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
