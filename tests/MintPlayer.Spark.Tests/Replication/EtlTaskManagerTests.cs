using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;
using NSubstitute;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Pins the self-loop guard in <see cref="EtlTaskManager.DeployAsync"/>: a deployment
/// whose <c>TargetDatabase</c> equals the local store's database must be refused —
/// otherwise a misrouted deployment (stale moduleInformations URL pointing back at
/// the requester) silently installs an ETL task that reads from and writes to the
/// same DB, producing an infinite RavenDB-internal write loop.
/// </summary>
public class EtlTaskManagerTests
{
    [Fact]
    public async Task DeployAsync_returns_failure_when_target_database_equals_local_store_database()
    {
        var documentStore = Substitute.For<IDocumentStore>();
        documentStore.Database.Returns("SparkHR");

        var manager = new EtlTaskManager(documentStore, NullLogger<EtlTaskManager>.Instance);

        var request = new EtlScriptRequest
        {
            RequestingModule = "HR",
            TargetDatabase = "SparkHR",
            TargetUrls = ["http://localhost:8080"],
            Scripts = [new EtlScriptItem { SourceCollection = "Cars", Script = "loadToCars(this);" }],
        };

        var result = await manager.DeployAsync(request);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("self-loop");
        result.TasksCreated.Should().Be(0);
        result.TasksUpdated.Should().Be(0);
    }

    [Fact]
    public async Task DeployAsync_self_loop_check_is_case_insensitive()
    {
        var documentStore = Substitute.For<IDocumentStore>();
        documentStore.Database.Returns("SparkHR");

        var manager = new EtlTaskManager(documentStore, NullLogger<EtlTaskManager>.Instance);

        var request = new EtlScriptRequest
        {
            RequestingModule = "HR",
            TargetDatabase = "sparkhr",
            TargetUrls = ["http://localhost:8080"],
            Scripts = [],
        };

        var result = await manager.DeployAsync(request);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("self-loop");
    }
}
