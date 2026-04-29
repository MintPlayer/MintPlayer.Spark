using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// Pins the public surface of <see cref="SparkExtensions"/> beyond <c>AddSpark/UseSpark</c>
/// itself: the configuration-aware <c>AddSpark</c> overload, the actions registration helper,
/// the <c>UseSpark(opts =&gt; ...)</c> options shape, and the model-synchronization helpers.
/// These are thin wrappers but each one is a discrete public API surface — a regression
/// breaks Demo apps that compose them in unique combinations.
/// </summary>
public class SparkExtensionsTests
{
    // --- AddSpark(IConfiguration) overload ------------------------------

    [Fact]
    public void AddSpark_with_configuration_binds_Spark_section_to_builder_options_before_configure_runs()
    {
        // The overload binds configuration.GetSection("Spark") to builder.Options *before*
        // invoking configure(builder). Pin that ordering — modules registered via configure
        // are entitled to read RavenDb settings off builder.Options.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spark:RavenDb:Database"] = "Bound",
                ["Spark:RavenDb:MaxConnectionRetries"] = "0",
                ["Spark:RavenDb:EnsureDatabaseCreated"] = "false",
            })
            .Build();

        SparkOptions? observedOptions = null;
        services.AddSpark(configuration, builder =>
        {
            observedOptions = ((SparkBuilder)builder).Options;
        });

        observedOptions.Should().NotBeNull();
        observedOptions!.RavenDb.Database.Should().Be("Bound");
        observedOptions.RavenDb.MaxConnectionRetries.Should().Be(0);
    }

    [Fact]
    public void AddSpark_with_configuration_invokes_the_configure_callback_with_a_builder()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var captured = false;

        services.AddSpark(configuration, builder =>
        {
            captured = true;
            builder.Should().NotBeNull();
            builder.Configuration.Should().BeSameAs(configuration);
        });

        captured.Should().BeTrue();
    }

    // --- AddSparkActions<TActions, TEntity> -----------------------------

    [Fact]
    public void AddSparkActions_registers_actions_class_under_the_typed_interface_and_concrete()
    {
        var services = new ServiceCollection();

        services.AddSparkActions<TestPersonActions, Person>();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IPersistentObjectActions<Person>) &&
            d.ImplementationType == typeof(TestPersonActions) &&
            d.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(d =>
            d.ServiceType == typeof(TestPersonActions) &&
            d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddSparkActions_returns_the_service_collection_for_chaining()
    {
        var services = new ServiceCollection();

        var returned = services.AddSparkActions<TestPersonActions, Person>();

        returned.Should().BeSameAs(services);
    }

    // --- SynchronizeSparkModels / SynchronizeSparkModelsIfRequested -----

    [Fact]
    public void SynchronizeSparkModelsIfRequested_returns_app_unchanged_when_flag_is_absent()
    {
        // The flag-PRESENT branch ends in Environment.Exit(0) — not testable in-process.
        // The flag-ABSENT path is the load-bearing one for normal startup (every Demo app
        // calls this on every boot to opt-in to schema sync).
        var app = SubstituteForApplicationBuilder();

        var returned = app.SynchronizeSparkModelsIfRequested<EmptyTestSparkContext>(
            ["--unrelated", "--verbose"]);

        returned.Should().BeSameAs(app);
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_with_empty_args_returns_app_unchanged()
    {
        var app = SubstituteForApplicationBuilder();

        var returned = app.SynchronizeSparkModelsIfRequested<EmptyTestSparkContext>([]);

        returned.Should().BeSameAs(app);
    }

    [Fact]
    public void SynchronizeSparkModels_in_Production_environment_returns_early_without_calling_the_synchronizer()
    {
        var synchronizer = Substitute.For<IModelSynchronizer>();
        var documentStore = Substitute.For<IDocumentStore>();
        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.EnvironmentName.Returns(Environments.Production);

        var app = SubstituteForApplicationBuilder(hostEnvironment, synchronizer, documentStore);

        var returned = app.SynchronizeSparkModels<EmptyTestSparkContext>();

        returned.Should().BeSameAs(app);
        synchronizer.DidNotReceiveWithAnyArgs().SynchronizeModels(default!);
    }

    [Fact]
    public void SynchronizeSparkModels_in_Development_environment_invokes_the_synchronizer_with_a_TContext_instance()
    {
        var synchronizer = Substitute.For<IModelSynchronizer>();
        var documentStore = Substitute.For<IDocumentStore>();
        documentStore.OpenAsyncSession().Returns(Substitute.For<IAsyncDocumentSession>());
        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.EnvironmentName.Returns(Environments.Development);

        var app = SubstituteForApplicationBuilder(hostEnvironment, synchronizer, documentStore);

        app.SynchronizeSparkModels<EmptyTestSparkContext>();

        synchronizer.Received(1).SynchronizeModels(Arg.Is<EmptyTestSparkContext>(c => c.Session != null));
    }

    // --- helpers --------------------------------------------------------

    private static IApplicationBuilder SubstituteForApplicationBuilder(
        IHostEnvironment? hostEnvironment = null,
        IModelSynchronizer? modelSynchronizer = null,
        IDocumentStore? documentStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(hostEnvironment ?? Substitute.For<IHostEnvironment>());
        services.AddSingleton(modelSynchronizer ?? Substitute.For<IModelSynchronizer>());
        services.AddSingleton(documentStore ?? Substitute.For<IDocumentStore>());
        // SparkModuleRegistry is required by the inner UseSpark (used by the options overload);
        // not invoked by the helpers tested here, but we keep the shape consistent.
        services.AddSingleton(new MintPlayer.Spark.Abstractions.Builder.SparkModuleRegistry());
        var sp = services.BuildServiceProvider();

        var app = Substitute.For<IApplicationBuilder>();
        app.ApplicationServices.Returns(sp);
        // Use(...) returns the same builder for chaining; pin that to avoid NREs on chained calls.
        app.Use(Arg.Any<Func<RequestDelegate, RequestDelegate>>()).Returns(app);
        return app;
    }

    public sealed class Person
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class TestPersonActions : DefaultPersistentObjectActions<Person>
    {
        public TestPersonActions(IEntityMapper mapper) : base(mapper) { }
    }

    public sealed class EmptyTestSparkContext : SparkContext { }
}
