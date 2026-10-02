using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Pins which indexes a <see cref="SparkEndpointFactory{TContext}"/> host deploys (M8, lever A).
/// </summary>
/// <remarks>
/// Deploying the whole test assembly's fixture indexes into every per-test database was 32% of this
/// project's thread time. The default now skips a nested fixture index of the test assembly unless the
/// fixture arms it, while top-level indexes keep deploying, which is what protects a consumer whose
/// context lives in its application assembly. Each rule is asserted against the database itself,
/// except the top-level one, which is asserted on the filter (see that test for why).
/// </remarks>
public class SparkEndpointFactoryIndexDeploymentTests : SparkTestDriver
{
    public sealed class DeployProbe
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    public class DeployProbes_Unarmed : AbstractIndexCreationTask<DeployProbe>
    {
        public DeployProbes_Unarmed()
        {
            Map = probes => from p in probes select new { p.Name };
        }
    }

    public class DeployProbes_Armed : AbstractIndexCreationTask<DeployProbe>
    {
        public DeployProbes_Armed()
        {
            Map = probes => from p in probes select new { p.Name };
        }
    }

    private async Task<string[]> DeployedIndexNamesAsync()
        => await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, int.MaxValue));

    [Fact]
    public async Task By_default_an_unarmed_nested_fixture_index_is_not_deployed()
    {
        await using var factory = new SparkEndpointFactory(Store, []);

        (await DeployedIndexNamesAsync()).Should().NotContain(new DeployProbes_Unarmed().IndexName);
    }

    [Fact]
    public async Task By_default_a_top_level_index_of_the_test_assembly_is_still_deployed()
    {
        // Stands in for a consumer's real application index: those are top-level classes and must keep
        // deploying with no action from the consumer.
        //
        // Asserted on the host's filter, not the database: the only way to assert it in the database
        // is a deployable top-level index in this assembly, and that index then deploys into every
        // host of every test (M8 item 13 measured ~58 ms of server CPU per boot for the one this
        // assembly used to have). The probe is abstract, so nothing deploys it; the filter judges a
        // type by where it is declared, not by whether it is abstract. The armed and unarmed cases
        // below still prove, in the database, that this filter is what decides deployment.
        typeof(TopLevelDeployProbe).DeclaringType.Should().BeNull();
        typeof(TopLevelDeployProbe).Assembly.Should().BeSameAs(typeof(TestSparkContext).Assembly);

        Func<Type, bool>? filter = null;
        await using var factory = new SparkEndpointFactory<TestSparkContext>(
            Store, [],
            configureSpark: spark => filter = spark.Registry.IndexDeploymentFilter);

        filter.Should().NotBeNull();
        filter!(typeof(TopLevelDeployProbe)).Should().BeTrue();
        filter(typeof(DeployProbes_Unarmed)).Should().BeFalse();
    }

    [Fact]
    public async Task An_index_armed_through_configureIndexCatalog_is_deployed()
    {
        await using var factory = new SparkEndpointFactory<TestSparkContext>(
            Store, [],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(DeployProbes_Armed)));

        var deployed = await DeployedIndexNamesAsync();
        deployed.Should().Contain(new DeployProbes_Armed().IndexName);
        deployed.Should().NotContain(new DeployProbes_Unarmed().IndexName);
    }

    [Fact]
    public async Task DeployAllIndexes_restores_deploying_every_index()
    {
        await using var factory = new SparkEndpointFactory<TestSparkContext>(Store, [], deployAllIndexes: true);

        var deployed = await DeployedIndexNamesAsync();
        deployed.Should().Contain(new DeployProbes_Unarmed().IndexName);
        deployed.Should().Contain(new DeployProbes_Armed().IndexName);
    }
}

/// <summary>
/// A top-level index type of the test assembly, for <see cref="SparkEndpointFactoryIndexDeploymentTests"/>.
/// Abstract on purpose: a deployable top-level index here would be deployed by every test host.
/// </summary>
public abstract class TopLevelDeployProbe : AbstractIndexCreationTask<SparkEndpointFactoryIndexDeploymentTests.DeployProbe>
{
}
