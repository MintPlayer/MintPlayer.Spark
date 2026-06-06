using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

namespace MintPlayer.Spark.Tests.DevTunnel;

/// <summary>
/// These extensions defer service registration: they call
/// <c>options.RegisterService(s => ...)</c>, and the registrations only execute
/// when <c>GitHubWebhooksOptions.ApplyRegistrations(services)</c> runs (during
/// <c>AddSparkGitHubWebhooks</c> composition). Tests drive the option + invoke
/// <c>ApplyRegistrations</c> directly.
/// </summary>
public class GitHubWebhooksDevTunnelExtensionsTests
{
    [Fact]
    public void AddSmeeDevTunnel_registers_the_smee_background_service_and_binds_the_channel_url()
    {
        var options = new GitHubWebhooksOptions();

        options.AddSmeeDevTunnel("https://smee.io/abc");

        var services = Register(options);
        var opts = services.GetRequiredService<IOptions<SmeeOptions>>().Value;
        opts.ChannelUrl.Should().Be("https://smee.io/abc");

        services.GetServices<IHostedService>().Should().ContainSingle(h => h is SmeeBackgroundService);
    }

    [Fact]
    public void AddWebSocketDevTunnel_registers_the_websocket_client_service_and_binds_both_options()
    {
        var options = new GitHubWebhooksOptions();

        options.AddWebSocketDevTunnel(
            productionWebSocketUrl: "wss://prod.example.com/spark/github/dev-ws",
            githubToken: "ghp_fake_token_123");

        var services = Register(options);
        var opts = services.GetRequiredService<IOptions<WebSocketDevTunnelOptions>>().Value;
        opts.ProductionWebSocketUrl.Should().Be("wss://prod.example.com/spark/github/dev-ws");
        opts.GitHubToken.Should().Be("ghp_fake_token_123");

        services.GetServices<IHostedService>().Should().ContainSingle(h => h is WebSocketDevClientService);
    }

    [Fact]
    public void Extension_methods_return_the_same_options_instance_for_fluent_chaining()
    {
        var options = new GitHubWebhooksOptions();

        var r1 = options.AddSmeeDevTunnel("https://smee.io/abc");
        var r2 = options.AddWebSocketDevTunnel("wss://x", "t");

        r1.Should().BeSameAs(options);
        r2.Should().BeSameAs(options);
    }

    [Fact]
    public void Calling_both_extensions_on_the_same_options_registers_both_hosted_services()
    {
        var options = new GitHubWebhooksOptions();
        options.AddSmeeDevTunnel("https://smee.io/abc");
        options.AddWebSocketDevTunnel("wss://x", "t");

        var services = Register(options);

        var hosted = services.GetServices<IHostedService>().ToList();
        hosted.Should().ContainSingle(h => h is SmeeBackgroundService);
        hosted.Should().ContainSingle(h => h is WebSocketDevClientService);
    }

    /// <summary>
    /// Builds the service provider the way GitHubWebhooksOptions.ApplyRegistrations would
    /// during Spark composition. ApplyRegistrations is internal so it's invoked via reflection.
    /// </summary>
    private static ServiceProvider Register(GitHubWebhooksOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Dependencies the hosted services will need at activation time.
        services.AddSingleton<Octokit.Webhooks.WebhookEventProcessor>(_ => new StubProcessor());

        var apply = typeof(GitHubWebhooksOptions).GetMethod(
            "ApplyRegistrations",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        apply.Invoke(options, [services]);

        return services.BuildServiceProvider();
    }

    private sealed class StubProcessor : Octokit.Webhooks.WebhookEventProcessor
    {
    }
}
