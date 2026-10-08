using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Endpoints;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.Services;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// Whether, and where, the dev WebSocket is mapped. It is a generator endpoint
/// (<see cref="DevWebSocketEndpoint"/>) whose <c>IsEnabled</c> keys on
/// <see cref="GitHubWebhooksOptions.DevelopmentAppId"/> and whose <c>GetPath</c> returns
/// <see cref="GitHubWebhooksOptions.DevWebSocketPath"/>; both are read once, at map time.
/// </summary>
public class DevWebSocketEndpointMappingTests
{
    private static async Task<IHost> StartAsync(
        Action<GitHubWebhooksOptions> configure,
        Action<IServiceCollection>? services = null)
    {
        var builder = new SparkBuilder(new ServiceCollection());
        builder.AddGithubWebhooks(o =>
        {
            o.WebhookSecret = "not-used-here";
            configure(o);
        });

        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    foreach (var descriptor in builder.Services)
                        s.Add(descriptor);
                    s.AddRouting();
                    services?.Invoke(s);
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => builder.Registry.MapEndpoints(endpoints));
                }))
            .StartAsync();
    }

    [Fact]
    public void The_options_default_is_the_endpoints_own_path()
    {
        new GitHubWebhooksOptions().DevWebSocketPath.Should().Be(DevWebSocketEndpoint.Path);
        DevWebSocketEndpoint.Path.Should().Be("/spark/github/dev-ws");
    }

    /// <summary>
    /// <see cref="IDevWebSocketService"/> is registered whatever the options say, and the endpoint
    /// <c>[Inject]</c>s it, so a disabled endpoint must not even be activated.
    /// The host registers a service that records its own construction, so the assertion is
    /// falsifiable: were the endpoint mapped (the <c>IsEnabled</c> gate removed), the upgrade below
    /// would reach it and its activation would construct the service.
    /// </summary>
    [Fact]
    public async Task Without_a_development_app_the_endpoint_is_not_mapped_and_its_service_never_resolved()
    {
        var constructed = 0;
        using var host = await StartAsync(
            _ => { },
            s => s.AddSingleton<IDevWebSocketService>(_ =>
            {
                Interlocked.Increment(ref constructed);
                throw new InvalidOperationException("IDevWebSocketService must not be resolved when the dev WebSocket is off.");
            }));

        var plain = await host.GetTestClient().GetAsync(DevWebSocketEndpoint.Path);
        var upgrade = () => host.GetTestServer().CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost" + DevWebSocketEndpoint.Path), CancellationToken.None);

        plain.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await upgrade.Should().ThrowAsync<InvalidOperationException>().WithMessage("*404*");
        host.Services.GetRequiredService<EndpointDataSource>().IsEndpointMapped<DevWebSocketEndpoint>().Should().BeFalse();
        constructed.Should().Be(0);
    }

    [Fact]
    public async Task With_a_development_app_the_endpoint_is_mapped_at_the_default_path()
    {
        using var host = await StartAsync(o => o.DevelopmentAppId = 1);

        var response = await host.GetTestClient().GetAsync(DevWebSocketEndpoint.Path);

        // 400, not 404: the route exists and refuses a request that is not a WebSocket upgrade.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Services.GetRequiredService<EndpointDataSource>().IsEndpointMapped<DevWebSocketEndpoint>().Should().BeTrue();
    }

    /// <summary>
    /// The bug: <c>AddGithubWebhooks</c> registered <see cref="IDevWebSocketService"/> only when its own
    /// options had a <c>DevelopmentAppId</c>, while <c>IsEnabled</c> reads <c>IOptions</c>. An app that
    /// set the id through <c>Configure&lt;GitHubWebhooksOptions&gt;()</c> got the endpoint mapped and
    /// every request to it failed to activate the endpoint.
    /// </summary>
    [Fact]
    public async Task A_development_app_set_through_Configure_maps_a_working_endpoint()
    {
        using var host = await StartAsync(
            _ => { },
            s => s.Configure<GitHubWebhooksOptions>(o => o.DevelopmentAppId = 1));

        var response = await host.GetTestClient().GetAsync(DevWebSocketEndpoint.Path);

        // 400, not 404 and not an activation failure: the endpoint was mapped and built.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_configured_path_moves_the_route()
    {
        using var host = await StartAsync(o =>
        {
            o.DevelopmentAppId = 1;
            o.DevWebSocketPath = "/custom/dev-socket";
        });
        var client = host.GetTestClient();

        (await client.GetAsync("/custom/dev-socket")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync(DevWebSocketEndpoint.Path)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The default path has no route parameters, so a configured one that adds any cannot bind and
    /// is refused when the routes are mapped — at startup, not on the first request.
    /// </summary>
    [Fact]
    public async Task A_configured_path_with_a_route_parameter_fails_at_startup()
    {
        var start = () => StartAsync(o =>
        {
            o.DevelopmentAppId = 1;
            o.DevWebSocketPath = "/spark/github/{tenant}/dev-ws";
        });

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DevWebSocketEndpoint*");
    }

    /// <summary>A disabled endpoint's <c>GetPath</c> is not called, so an invalid path cannot fail a host that does not use it.</summary>
    [Fact]
    public async Task A_bad_path_is_ignored_while_the_endpoint_is_off()
    {
        using var host = await StartAsync(o => o.DevWebSocketPath = "/spark/github/{tenant}/dev-ws");

        host.Services.GetRequiredService<EndpointDataSource>().IsEndpointMapped<DevWebSocketEndpoint>().Should().BeFalse();
    }
}
