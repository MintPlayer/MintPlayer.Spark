using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks;
using MintPlayer.Spark.Webhooks.Endpoints;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Webhooks;

/// <summary>
/// <c>POST /spark/webhooks/{source}</c> over HTTP: mapped only when a source uses it, the body read
/// raw (not bound as JSON), a fetch-by-id delivery accepted as a form post, and every refusal a bare
/// status. The bus is a substitute; <see cref="WebhookReceiverTests"/> covers the real queue.
/// </summary>
public class ReceiveWebhookEndpointTests
{
    private static async Task<(IHost Host, IMessageBus Bus)> StartAsync(Action<ISparkBuilder> configure)
    {
        var bus = Substitute.For<IMessageBus>();
        var builder = new SparkBuilder(new ServiceCollection());
        configure(builder);

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    foreach (var descriptor in builder.Services)
                        s.Add(descriptor);
                    s.AddRouting();
                    s.AddSingleton(bus);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => builder.Registry.MapEndpoints(endpoints));
                }))
            .StartAsync();

        return (host, bus);
    }

    private static void Mollie(ISparkBuilder spark) => spark.AddWebhookSource("mollie", source =>
    {
        source.Verification = _ => new FetchByIdStrategy();
        source.UseHandler<NoopHandler>();
    });

    private static HttpContent Form(string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");
        return content;
    }

    [Fact]
    public async Task A_fetch_by_id_delivery_is_enqueued_as_an_unverified_hint()
    {
        var (host, bus) = await StartAsync(Mollie);
        using var running = host;

        var response = await host.GetTestClient().PostAsync("/spark/webhooks/mollie", Form("id=tr_WDqYK6vllg"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await bus.Received(1).BroadcastAsync(
            Arg.Is<WebhookDelivery>(d => d.Source == "mollie" && d.ResourceId == "tr_WDqYK6vllg" && !d.PayloadVerified),
            Arg.Any<BroadcastOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_delivery_without_an_id_is_refused_with_401()
    {
        var (host, bus) = await StartAsync(Mollie);
        using var running = host;

        var response = await host.GetTestClient().PostAsync("/spark/webhooks/mollie", Form("status=paid"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        bus.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_source_is_404()
    {
        var (host, bus) = await StartAsync(Mollie);
        using var running = host;

        var response = await host.GetTestClient().PostAsync("/spark/webhooks/stripe", Form("id=tr_1"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        bus.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversized_body_is_413()
    {
        var (host, bus) = await StartAsync(spark => spark.AddWebhookSource("small", source =>
        {
            source.Verification = _ => new FetchByIdStrategy();
            source.MaxBodyBytes = 8;
            source.UseHandler<NoopHandler>();
        }));
        using var running = host;

        var response = await host.GetTestClient().PostAsync("/spark/webhooks/small", Form("id=tr_1234567890"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        bus.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_shared_route_source_the_route_is_not_mapped()
    {
        var (host, _) = await StartAsync(spark => spark.AddWebhookSource("own", source =>
        {
            source.Verification = _ => new FetchByIdStrategy();
            source.SharedRoute = false;
            source.UseHandler<NoopHandler>();
        }));
        using var running = host;

        host.Services.GetRequiredService<EndpointDataSource>().IsEndpointMapped<ReceiveWebhook>().Should().BeFalse();
        (await host.GetTestClient().PostAsync("/spark/webhooks/own", Form("id=tr_1"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class NoopHandler : IWebhookHandler
    {
        public Task HandleAsync(WebhookDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
