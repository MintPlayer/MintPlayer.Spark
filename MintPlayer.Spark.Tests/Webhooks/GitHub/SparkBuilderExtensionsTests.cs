using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// AddGithubWebhooks is the only public entry point for the package — every consumer wires
/// the integration through it. Pins option propagation, the conditional dev-WebSocket
/// registration, the deferred-registration extensibility hook, and the registry handoff so
/// regressions are caught without a full Demo boot.
/// </summary>
public class SparkBuilderExtensionsTests
{
    private static SparkBuilder NewBuilder() => new(new ServiceCollection());

    private static GitHubWebhooksOptions ResolveOptions(SparkBuilder builder)
        => builder.Services.BuildServiceProvider().GetRequiredService<IOptions<GitHubWebhooksOptions>>().Value;

    [Fact]
    public void AddGithubWebhooks_invokes_configure_callback_with_fresh_options()
    {
        var builder = NewBuilder();
        GitHubWebhooksOptions? captured = null;

        builder.AddGithubWebhooks(opt => captured = opt);

        captured.Should().NotBeNull();
        captured!.WebhookPath.Should().Be("/api/github/webhooks"); // default
    }

    [Fact]
    public void AddGithubWebhooks_returns_same_builder_for_chaining()
    {
        var builder = NewBuilder();

        var returned = builder.AddGithubWebhooks(_ => { });

        returned.Should().BeSameAs(builder);
    }

    [Fact]
    public void AddGithubWebhooks_propagates_every_configured_option_to_IOptions()
    {
        var builder = NewBuilder();

        builder.AddGithubWebhooks(opt =>
        {
            opt.WebhookSecret = "shh";
            opt.WebhookPath = "/custom/hook";
            opt.ProductionAppId = 111L;
            opt.DevelopmentAppId = 222L;
            opt.DevWebSocketPath = "/dev/ws";
            opt.AllowedDevUsers = ["alice", "bob"];
            opt.ClientId = "iv1.abc";
            opt.PrivateKeyPem = "-----BEGIN-----";
            opt.PrivateKeyPath = "/tmp/key.pem";
        });

        var resolved = ResolveOptions(builder);
        resolved.WebhookSecret.Should().Be("shh");
        resolved.WebhookPath.Should().Be("/custom/hook");
        resolved.ProductionAppId.Should().Be(111L);
        resolved.DevelopmentAppId.Should().Be(222L);
        resolved.DevWebSocketPath.Should().Be("/dev/ws");
        resolved.AllowedDevUsers.Should().BeEquivalentTo(["alice", "bob"]);
        resolved.ClientId.Should().Be("iv1.abc");
        resolved.PrivateKeyPem.Should().Be("-----BEGIN-----");
        resolved.PrivateKeyPath.Should().Be("/tmp/key.pem");
    }

    [Fact]
    public void AddGithubWebhooks_registers_core_webhook_services_via_source_generator()
    {
        var builder = NewBuilder();

        builder.AddGithubWebhooks(_ => { });

        // Source-generated AddSparkWebhooksGitHubServices() pulls in everything tagged [Register].
        // We assert at the descriptor level — resolving WebhookEventProcessor would also
        // require IMessageBus, which is wired separately by AddMessaging().
        var serviceTypes = builder.Services.Select(d => d.ServiceType).ToHashSet();
        serviceTypes.Should().Contain(typeof(IGitHubClientFactory));
        serviceTypes.Should().Contain(typeof(IGitHubInstallationService));
        serviceTypes.Should().Contain(typeof(ISignatureService));
        serviceTypes.Should().Contain(typeof(WebhookEventProcessor));
    }

    [Fact]
    public void AddGithubWebhooks_does_not_register_DevWebSocketService_without_DevelopmentAppId()
    {
        var builder = NewBuilder();

        builder.AddGithubWebhooks(_ => { });

        builder.Services.Should().NotContain(d => d.ServiceType == typeof(IDevWebSocketService));
    }

    [Fact]
    public void AddGithubWebhooks_registers_DevWebSocketService_when_DevelopmentAppId_set()
    {
        var builder = NewBuilder();

        builder.AddGithubWebhooks(opt => opt.DevelopmentAppId = 12345L);

        var descriptor = builder.Services.Should().ContainSingle(d => d.ServiceType == typeof(IDevWebSocketService)).Subject;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<DevWebSocketService>();
    }

    [Fact]
    public void AddGithubWebhooks_applies_deferred_registrations_added_via_RegisterService()
    {
        var builder = NewBuilder();
        var marker = new Marker();

        builder.AddGithubWebhooks(opt =>
            opt.RegisterService(svc => svc.AddSingleton(marker)));

        builder.Services.BuildServiceProvider().GetService<Marker>().Should().BeSameAs(marker);
    }

    [Fact]
    public void AddGithubWebhooks_registers_exactly_one_endpoint_action_with_the_module_registry()
    {
        var builder = NewBuilder();
        var endpoints = new RecordingEndpointBuilder();

        builder.AddGithubWebhooks(_ => { });

        // Until MapEndpoints runs, the endpoint builder hasn't been touched.
        endpoints.Invocations.Should().Be(0);

        builder.Registry.MapEndpoints(endpoints);

        // The action invokes endpoints.MapGitHubWebhooks(...) which under the hood
        // calls IEndpointRouteBuilder.DataSources/CreateApplicationBuilder; we don't
        // assert on that — just that the registry-stored action runs.
        endpoints.Invocations.Should().BeGreaterThan(0);
    }

    private sealed class Marker { }

    /// <summary>
    /// Bare-bones <see cref="IEndpointRouteBuilder"/> stand-in. We need a real instance
    /// because <see cref="Octokit.Webhooks.AspNetCore.EndpointRouteBuilderExtensions.MapGitHubWebhooks"/>
    /// pokes at <see cref="IEndpointRouteBuilder.ServiceProvider"/> and <see cref="IEndpointRouteBuilder.DataSources"/>;
    /// substitutes throw on the property reads. This implementation returns the empty
    /// minimum needed to let the registration code run end-to-end.
    /// </summary>
    private sealed class RecordingEndpointBuilder : IEndpointRouteBuilder
    {
        private readonly List<EndpointDataSource> _dataSources = [];
        private readonly ServiceProvider _serviceProvider;
        public int Invocations { get; private set; }

        public RecordingEndpointBuilder()
        {
            var services = new ServiceCollection();
            services.AddRouting();
            _serviceProvider = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider
        {
            get { Invocations++; return _serviceProvider; }
        }

        public ICollection<EndpointDataSource> DataSources
        {
            get { Invocations++; return _dataSources; }
        }

        public IApplicationBuilder CreateApplicationBuilder()
        {
            Invocations++;
            return new ApplicationBuilder(_serviceProvider);
        }
    }
}
