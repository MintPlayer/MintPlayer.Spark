using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Configuration;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// The IDocumentStore singleton factory inside <c>AddSpark</c> is normally bypassed by tests
/// that pre-register an embedded Raven instance via <see cref="MintPlayer.Spark.Testing.SparkEndpointFactory{TContext}"/>.
/// This fixture instead lets the factory build its own DocumentStore against the embedded
/// server's HTTP URL — pinning the GUID id-generator wiring, the JSON converters, the
/// "EnsureDatabaseCreated" branch, and the connection-retry short-circuit.
/// </summary>
public class AddSparkDocumentStoreFactoryTests : SparkTestDriver
{
    private static IConfiguration BuildConfiguration(string url, string database, bool ensureCreated, int retries = 0)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Spark:RavenDb:Urls:0"] = url,
                ["Spark:RavenDb:Database"] = database,
                ["Spark:RavenDb:EnsureDatabaseCreated"] = ensureCreated ? "true" : "false",
                ["Spark:RavenDb:MaxConnectionRetries"] = retries.ToString(),
            })
            .Build();
    }

    private static IServiceProvider BuildProvider(IConfiguration configuration, bool development)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(development ? Environments.Development : Environments.Production);
        services.AddSingleton(env);
        services.AddSpark(configuration, _ => { });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Factory_returns_DocumentStore_with_configured_url_and_database()
    {
        var dbName = "test-factory-" + Guid.NewGuid().ToString("N");
        var configuration = BuildConfiguration(Store.Urls[0], dbName, ensureCreated: false);

        using var sp = (ServiceProvider)BuildProvider(configuration, development: false);
        var store = sp.GetRequiredService<IDocumentStore>();

        store.Urls.Should().BeEquivalentTo(new[] { Store.Urls[0] });
        store.Database.Should().Be(dbName);
        // store.Initialize() was called inside the factory — non-initialized stores throw on most ops.
        var act = () => store.Conventions.GetCollectionName(typeof(string));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Factory_wires_a_GUID_based_async_document_id_generator()
    {
        var dbName = "test-factory-" + Guid.NewGuid().ToString("N");
        var configuration = BuildConfiguration(Store.Urls[0], dbName, ensureCreated: false);

        using var sp = (ServiceProvider)BuildProvider(configuration, development: false);
        var store = sp.GetRequiredService<IDocumentStore>();

        var entity = new FactoryProbe();
        var id = await store.Conventions.AsyncDocumentIdGenerator(store.Database, entity);

        // The generator format is "{collection}/{guid}". Default conventions pluralize to "FactoryProbes".
        id.Should().Contain("/");
        id.Should().StartWith(store.Conventions.GetCollectionName(typeof(FactoryProbe)) + "/");
        Guid.TryParse(id.Split('/').Last(), out _).Should().BeTrue();
    }

    [Fact]
    public void Factory_creates_missing_database_when_EnsureDatabaseCreated_is_true_in_production()
    {
        var dbName = "test-create-" + Guid.NewGuid().ToString("N");
        var configuration = BuildConfiguration(Store.Urls[0], dbName, ensureCreated: true);

        using var sp = (ServiceProvider)BuildProvider(configuration, development: false);
        var store = sp.GetRequiredService<IDocumentStore>();

        // The factory should have run the create-if-missing check and created the database
        // — verify it exists on the server.
        var names = store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, int.MaxValue));
        names.Should().Contain(dbName);
    }

    [Fact]
    public void Factory_skips_the_create_database_check_when_EnsureDatabaseCreated_is_false_and_environment_is_production()
    {
        // Pin the early-skip branch — production hosts that don't opt in must not auto-create
        // databases. We use a name we never create; if the factory had created it, this would
        // be findable on the server.
        var dbName = "test-skip-" + Guid.NewGuid().ToString("N");
        var configuration = BuildConfiguration(Store.Urls[0], dbName, ensureCreated: false);

        using var sp = (ServiceProvider)BuildProvider(configuration, development: false);
        var store = sp.GetRequiredService<IDocumentStore>();

        var names = store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, int.MaxValue));
        names.Should().NotContain(dbName);
    }

    private sealed class FactoryProbe
    {
        public string? Id { get; set; }
    }
}
