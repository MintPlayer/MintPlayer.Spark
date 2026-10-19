using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// The refusals in <see cref="EtlTaskManager.DeployAsync"/> that return before any ETL call: the
/// self-loop guard (a target database equal to the local one would make RavenDB write into its own
/// source for ever) and the licence check (F18).
/// </summary>
public class EtlTaskManagerTests
{
    private static EtlDeployment Deployment(string targetDatabase) => new(
        "HR",
        targetDatabase,
        ["http://localhost:8080"],
        [new EtlTransformScript("cars-v1-Cars", "Cars", "loadToCars({\n    Plate: this.Plate\n});\n")]);

    private static (EtlTaskManager Manager, IDocumentStore Store, IRavenEtlLicence Licence) NewManager(bool? hasEtl = true)
    {
        var documentStore = Substitute.For<IDocumentStore>();
        documentStore.Database.Returns("SparkHR");
        var licence = Substitute.For<IRavenEtlLicence>();
        licence.HasRavenEtlAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(hasEtl));
        return (new EtlTaskManager(documentStore, NullLogger<EtlTaskManager>.Instance, licence), documentStore, licence);
    }

    [Fact]
    public async Task DeployAsync_returns_failure_when_target_database_equals_local_store_database()
    {
        var (manager, _, _) = NewManager();

        var result = await manager.DeployAsync(Deployment("SparkHR"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("self-loop");
        result.TasksCreated.Should().Be(0);
        result.TasksUpdated.Should().Be(0);
    }

    [Fact]
    public async Task DeployAsync_self_loop_check_is_case_insensitive()
    {
        var (manager, _, _) = NewManager();

        var result = await manager.DeployAsync(Deployment("sparkhr"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("self-loop");
    }

    /// <summary>
    /// F18, red before the fix: on a licence without ETL (Community) the deployment reached
    /// <c>AddEtlOperation</c>, failed with a generic <c>ETL_DEPLOY_FAILED</c>, and the consumer retried
    /// it until the message dead-lettered. Now the licence is read first and the refusal says why,
    /// without touching the database.
    /// </summary>
    [Fact]
    public async Task DeployAsync_refuses_with_NotLicensed_when_the_licence_has_no_ETL()
    {
        var (manager, store, _) = NewManager(hasEtl: false);

        var result = await manager.DeployAsync(Deployment("hr-db"));

        result.Success.Should().BeFalse();
        result.Error.Should().Be(EtlDeploymentResult.NotLicensedError);
        _ = store.DidNotReceive().Maintenance;
    }
}
