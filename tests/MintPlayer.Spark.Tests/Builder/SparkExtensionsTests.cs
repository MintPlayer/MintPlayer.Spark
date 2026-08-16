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

    // --- SynchronizeSparkModelsIfRequested (builder phase) --------------

    [Fact]
    public void SynchronizeSparkModelsIfRequested_returns_false_when_the_flag_is_absent()
    {
        // False means "no command was handled, carry on and start the app" — the path every
        // boot takes. It must not touch the model directory.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, EmptyTestSparkContext>();

        var handled = builder.SynchronizeSparkModelsIfRequested(["--unrelated", "--verbose"]);

        handled.Should().BeFalse();
        Directory.Exists(Path.Combine(scratch.Path, "App_Data", "Model")).Should().BeFalse(
            "no command was requested, so nothing should have been written");
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_with_empty_args_returns_false()
    {
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        builder.SynchronizeSparkModelsIfRequested([]).Should().BeFalse();
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_writes_the_model_without_any_database()
    {
        // The point of the builder-phase move: no IDocumentStore is ever resolved, so this runs
        // in CI where no RavenDB exists. If a connection creeps back in, this test hangs or throws
        // rather than passing quietly.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();
        builder.Services.AddScoped<SparkContext, OneEntityTestSparkContext>();

        var handled = builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);

        handled.Should().BeTrue("the command was handled, so the host must return instead of starting");
        Environment.ExitCode.Should().Be(0);
        File.Exists(Path.Combine(scratch.Path, "App_Data", "Model", "SyncProbe.json")).Should().BeTrue();
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_reports_a_missing_context_registration_and_fails_the_run()
    {
        // A merge queue must not see exit 0 from a run that did nothing — that is a green gate
        // that never ran, which is the failure mode this whole change exists to remove.
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        var handled = builder.SynchronizeSparkModelsIfRequested(["--spark-synchronize-model"]);

        handled.Should().BeTrue();
        Environment.ExitCode.Should().Be(2);
    }

    [Fact]
    public void SynchronizeSparkModelsIfRequested_generic_overload_does_not_need_a_registration()
    {
        using var scratch = new ScratchContentRoot();
        var builder = scratch.CreateBuilder();

        var handled = builder.SynchronizeSparkModelsIfRequested<OneEntityTestSparkContext>(
            ["--spark-synchronize-model"]);

        handled.Should().BeTrue();
        File.Exists(Path.Combine(scratch.Path, "App_Data", "Model", "SyncProbe.json")).Should().BeTrue();
    }

    // --- IModelSynchronizer is a development-only service ----------------

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void AddSpark_does_not_register_the_model_synchronizer_outside_Development(string environmentName)
    {
        // The security property, made structural: outside Development there is nothing in the
        // container to resolve, so app code cannot drive a model rewrite by reaching past the
        // build-time command. Previously [Register] put it in every app in every environment.
        using var scratch = new ScratchContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = scratch.Path,
            EnvironmentName = environmentName,
        });

        builder.Services.AddSpark(spark => spark.UseContext<EmptyTestSparkContext>());

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetService<IModelSynchronizer>().Should().BeNull(
            $"the synchronizer is a build-time tool and must not be resolvable in {environmentName}");
    }

    [Fact]
    public void AddSpark_registers_the_model_synchronizer_in_Development()
    {
        using var scratch = new ScratchContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = scratch.Path,
            EnvironmentName = Environments.Development,
        });

        builder.Services.AddSpark(spark => spark.UseContext<EmptyTestSparkContext>());

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetService<IModelSynchronizer>().Should().NotBeNull();
    }

    // --- helpers --------------------------------------------------------

    /// <summary>
    /// A throwaway content root plus a <see cref="WebApplicationBuilder"/> rooted at it, so
    /// synchronization writes into the temp directory rather than the test host's own folder.
    /// Also restores <see cref="Environment.ExitCode"/>, which these tests deliberately set.
    /// </summary>
    private sealed class ScratchContentRoot : IDisposable
    {
        private readonly int _previousExitCode = Environment.ExitCode;

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "spark-sync-tests-" + Guid.NewGuid().ToString("N"));

        public ScratchContentRoot() => Directory.CreateDirectory(Path);

        public WebApplicationBuilder CreateBuilder() =>
            WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path });

        public void Dispose()
        {
            Environment.ExitCode = _previousExitCode;
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
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

    public sealed class SyncProbe
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>
    /// Declares one queryable root so synchronization has something to write. The getter is never
    /// invoked — only its property type is read — which is why a null <c>Session</c> is safe here.
    /// </summary>
    public sealed class OneEntityTestSparkContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<SyncProbe> SyncProbes => Session.Query<SyncProbe>();
    }
}
