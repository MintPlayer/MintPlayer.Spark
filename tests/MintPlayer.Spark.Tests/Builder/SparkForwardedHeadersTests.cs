using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Spike S-FH1 (#460, D15): Spark's forwarded-headers defaults, through the real
/// <c>ForwardedHeadersMiddleware</c> and the startup filter that places it.
/// </summary>
/// <remarks>
/// The pipeline is assembled from the registered <see cref="IStartupFilter"/>s exactly as the
/// web host assembles it, and invoked with a hand-built <see cref="HttpContext"/>, because the
/// transport peer is the whole subject and a test server does not let a test choose it.
/// </remarks>
public class SparkForwardedHeadersTests
{
    private static readonly IPAddress DockerProxy = IPAddress.Parse("172.18.0.2");
    private static readonly IPAddress PublicCaller = IPAddress.Parse("198.51.100.9");

    private static RequestDelegate Pipeline(
        Dictionary<string, string?>? configuration = null,
        string environment = "Production",
        Action<IServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build());
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment });
        services.AddLogging();
        services.AddSparkForwardedHeaders();
        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        Action<IApplicationBuilder> configure = a => a.Run(_ => Task.CompletedTask);
        foreach (var filter in provider.GetServices<IStartupFilter>().Reverse())
            configure = filter.Configure(configure);

        configure(app);
        return app.Build();
    }

    private static async Task<HttpContext> SendAsync(
        RequestDelegate pipeline, IPAddress peer, string? forwardedFor = null, string? proto = null, string? host = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("internal:8080");
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (proto is not null) context.Request.Headers["X-Forwarded-Proto"] = proto;
        if (host is not null) context.Request.Headers["X-Forwarded-Host"] = host;

        await pipeline(context);
        return context;
    }

    [Fact]
    public async Task A_spoofed_leftmost_entry_is_ignored_behind_one_trusted_proxy()
    {
        // The client wrote 6.6.6.6; the proxy appended the real peer. One hop reads only the
        // rightmost entry — the one the proxy wrote.
        var context = await SendAsync(Pipeline(), DockerProxy, forwardedFor: "6.6.6.6, 203.0.113.7");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.7"));
    }

    [Fact]
    public async Task An_untrusted_sender_keeps_its_socket_address()
    {
        var context = await SendAsync(Pipeline(), PublicCaller, forwardedFor: "6.6.6.6", proto: "https");

        context.Connection.RemoteIpAddress.Should().Be(PublicCaller);
        context.Request.Scheme.Should().Be("http");
    }

    [Fact]
    public async Task Two_proxy_hops_read_two_entries_and_no_more()
    {
        var pipeline = Pipeline(new() { ["Spark:ForwardedHeaders:ProxyHops"] = "2" });

        // Entry proxy 10.0.0.5 appended the client; the inner proxy appended 10.0.0.5.
        var context = await SendAsync(pipeline, DockerProxy, forwardedFor: "6.6.6.6, 203.0.113.7, 10.0.0.5");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.7"));
    }

    [Fact]
    public async Task The_scheme_is_forwarded_from_a_trusted_proxy()
    {
        var context = await SendAsync(Pipeline(), DockerProxy, forwardedFor: "203.0.113.7", proto: "https");

        context.Request.Scheme.Should().Be("https");
    }

    [Fact]
    public async Task The_host_is_not_forwarded_by_default()
    {
        var context = await SendAsync(Pipeline(), DockerProxy, forwardedFor: "203.0.113.7", host: "evil.example");

        context.Request.Host.Value.Should().Be("internal:8080");
    }

    [Fact]
    public async Task Configured_proxies_replace_the_private_range_default()
    {
        var pipeline = Pipeline(new() { ["Spark:ForwardedHeaders:KnownProxies:0"] = "203.0.113.1" });

        var fromDocker = await SendAsync(pipeline, DockerProxy, forwardedFor: "6.6.6.6");
        var fromProxy = await SendAsync(pipeline, IPAddress.Parse("203.0.113.1"), forwardedFor: "203.0.113.7");

        fromDocker.Connection.RemoteIpAddress.Should().Be(DockerProxy);
        fromProxy.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("203.0.113.7"));
    }

    /// <summary>
    /// The code every demo app used to carry. Clearing both lists means "validate nothing", not "no
    /// proxies" — refused at startup outside Development whatever sets it.
    /// </summary>
    [Fact]
    public void Trusting_every_sender_refuses_to_start_outside_Development()
    {
        var act = () => Pipeline(configureServices: ClearTrust);

        act.Should().Throw<InvalidOperationException>().WithMessage("*trusts every sender*");
    }

    [Fact]
    public async Task Trusting_every_sender_is_allowed_in_Development()
    {
        var pipeline = Pipeline(environment: Environments.Development, configureServices: ClearTrust);

        var context = await SendAsync(pipeline, PublicCaller, forwardedFor: "6.6.6.6");

        context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse("6.6.6.6"));
    }

    [Fact]
    public void Zero_proxy_hops_refuses_to_start()
    {
        var act = () => Pipeline(new() { ["Spark:ForwardedHeaders:ProxyHops"] = "0" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*ProxyHops*");
    }

    [Fact]
    public void An_unlimited_forward_limit_refuses_to_start()
    {
        var act = () => Pipeline(configureServices: s => s.Configure<ForwardedHeadersOptions>(o => o.ForwardLimit = null));

        act.Should().Throw<InvalidOperationException>().WithMessage("*ForwardLimit*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    public void Forwarding_the_host_refuses_to_start_while_any_host_is_allowed(string? allowedHosts)
    {
        var act = () => Pipeline(new()
        {
            ["Spark:ForwardedHeaders:ForwardHost"] = "true",
            ["AllowedHosts"] = allowedHosts,
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedHosts*");
    }

    [Fact]
    public async Task Forwarding_the_host_is_restricted_to_the_allowed_hosts()
    {
        var pipeline = Pipeline(new()
        {
            ["Spark:ForwardedHeaders:ForwardHost"] = "true",
            ["AllowedHosts"] = "coverage.example;www.coverage.example",
        });

        var allowed = await SendAsync(pipeline, DockerProxy, forwardedFor: "203.0.113.7", host: "coverage.example");
        var refused = await SendAsync(pipeline, DockerProxy, forwardedFor: "203.0.113.7", host: "evil.example");

        allowed.Request.Host.Value.Should().Be("coverage.example");
        refused.Request.Host.Value.Should().Be("internal:8080");
    }

    private static void ClearTrust(IServiceCollection services)
        => services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });
}
