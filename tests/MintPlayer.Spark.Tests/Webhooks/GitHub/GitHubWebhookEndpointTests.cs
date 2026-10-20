using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Endpoints;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using Octokit.Webhooks;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// The GitHub webhook POST as a source of <c>MintPlayer.Spark.Webhooks</c>
/// (<see cref="GitHubWebhookEndpoint"/>), replacing Octokit's hand-mapped <c>MapGitHubWebhooks</c>.
/// The signature is now refused at the request (401) over the exact bytes received, and a verified
/// delivery reaches the webhook processor unchanged. The processor is replaced by a recorder, so these
/// tests are about the route, not about what the processor broadcasts
/// (<see cref="SparkWebhookEventProcessorTests"/> covers that).
/// </summary>
public class GitHubWebhookEndpointTests
{
    private const string Secret = "github-endpoint-secret";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        """{"action":"opened","number":1,"ratio":1.50,"url":"a\/b","created_at":"2026-08-12T08:45:12.000+02:00"}""");

    private static async Task<(IHost Host, RecordingProcessor Processor)> StartAsync(Action<GitHubWebhooksOptions>? configure = null)
    {
        var processor = new RecordingProcessor();
        var builder = new SparkBuilder(new ServiceCollection());
        builder.AddGithubWebhooks(o =>
        {
            o.WebhookSecret = Secret;
            configure?.Invoke(o);
        });

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    foreach (var descriptor in builder.Services)
                        s.Add(descriptor);
                    s.AddRouting();
                    // Last registration wins: the handler's WebhookEventProcessor is the recorder.
                    s.AddScoped<WebhookEventProcessor>(_ => processor);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => builder.Registry.MapEndpoints(endpoints));
                }))
            .StartAsync();

        return (host, processor);
    }

    private static string Sign(byte[] body, string secret = Secret)
        => "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static HttpRequestMessage Delivery(string path, byte[] body, string? signature, string contentType = "application/json")
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", "72d3162e-cc78-11e3-81ab-4c9367dc0958");
        if (signature is not null)
            request.Headers.Add("X-Hub-Signature-256", signature);
        return request;
    }

    [Fact]
    public async Task A_correctly_signed_delivery_reaches_the_processor_with_its_exact_body()
    {
        var (host, processor) = await StartAsync();
        using var running = host;

        var response = await host.GetTestClient().SendAsync(Delivery(GitHubWebhookEndpoint.DefaultPath, Body, Sign(Body)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = processor.Calls.Should().ContainSingle().Which;
        Encoding.UTF8.GetBytes(call.Body).Should().Equal(Body);
        call.Headers["X-GitHub-Event"].ToString().Should().Be("issues");
        call.Headers["x-github-delivery"].ToString().Should().Be("72d3162e-cc78-11e3-81ab-4c9367dc0958",
            "the processor reads headers case-insensitively");
    }

    [Fact]
    public async Task A_tampered_delivery_is_refused_with_401_before_the_processor()
    {
        var (host, processor) = await StartAsync();
        using var running = host;
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body).Replace("1.50", "1.5"));
        var client = host.GetTestClient();

        (await client.SendAsync(Delivery(GitHubWebhookEndpoint.DefaultPath, tampered, Sign(Body)))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Delivery(GitHubWebhookEndpoint.DefaultPath, Body, signature: null))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Delivery(GitHubWebhookEndpoint.DefaultPath, Body, Sign(Body, "another-secret")))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        processor.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Octokit skipped its check when the secret was empty, and only Spark's second check inside the
    /// processor dropped the delivery, after a 200. Now the request itself fails closed.
    /// </summary>
    [Fact]
    public async Task Without_a_secret_every_delivery_is_refused_with_401()
    {
        var (host, processor) = await StartAsync(o => o.WebhookSecret = string.Empty);
        using var running = host;

        var response = await host.GetTestClient().SendAsync(Delivery(GitHubWebhookEndpoint.DefaultPath, Body, Sign(Body, string.Empty)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        processor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused_with_415()
    {
        var (host, processor) = await StartAsync();
        using var running = host;

        var response = await host.GetTestClient().SendAsync(
            Delivery(GitHubWebhookEndpoint.DefaultPath, Body, Sign(Body), "application/x-www-form-urlencoded"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        processor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_configured_path_moves_the_route()
    {
        var (host, processor) = await StartAsync(o => o.WebhookPath = "/custom/hook");
        using var running = host;
        var client = host.GetTestClient();

        (await client.SendAsync(Delivery("/custom/hook", Body, Sign(Body)))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(Delivery(GitHubWebhookEndpoint.DefaultPath, Body, Sign(Body)))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        processor.Calls.Should().ContainSingle();
    }

    /// <summary>
    /// GitHub keeps its own route, so the shared <c>/spark/webhooks/{source}</c> route is not mapped
    /// for it: an app with only GitHub has no shared route at all.
    /// </summary>
    [Fact]
    public async Task GitHub_is_not_served_by_the_shared_route()
    {
        var (host, processor) = await StartAsync();
        using var running = host;

        var response = await host.GetTestClient().SendAsync(Delivery("/spark/webhooks/github", Body, Sign(Body)));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        processor.Calls.Should().BeEmpty();
        host.Services.GetRequiredService<EndpointDataSource>().IsEndpointMapped<GitHubWebhookEndpoint>().Should().BeTrue();
    }

    private sealed class RecordingProcessor : WebhookEventProcessor
    {
        public List<(IDictionary<string, StringValues> Headers, string Body)> Calls { get; } = [];

        public override ValueTask ProcessWebhookAsync(IDictionary<string, StringValues> headers, string body, CancellationToken cancellationToken = default)
        {
            Calls.Add((headers, body));
            return ValueTask.CompletedTask;
        }
    }
}
