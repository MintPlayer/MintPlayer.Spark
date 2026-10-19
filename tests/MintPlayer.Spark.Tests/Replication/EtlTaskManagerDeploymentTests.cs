using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.OngoingTasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// <see cref="EtlTaskManager"/> against a real RavenDB ETL, with a second database on the same
/// embedded server as the target. <see cref="EtlTaskManagerTests"/> covers only the self-loop
/// refusal, which returns before any ETL call is made.
/// <para>
/// A RavenDB ETL task needs a licence that includes it (Developer does, Community does not). Under
/// a licence without it the first deployment fails on the licence, so the create-then-update test
/// stops there (CI runs the Developer licence, which covers it); the lookup-failure test does not
/// need an ETL to exist and runs under either.
/// </para>
/// </summary>
public class EtlTaskManagerDeploymentTests : SparkTestDriver
{
    private const string Module = "HR";
    private readonly string _targetDatabase = $"EtlTarget-{Guid.NewGuid():N}";
    private readonly RecordingLogger _log = new();

    private sealed class RecordingLogger : ILogger<EtlTaskManager>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, exception));
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(_targetDatabase)));
    }

    public override async Task DisposeAsync()
    {
        Store.Maintenance.Server.Send(new DeleteDatabasesOperation(_targetDatabase, hardDelete: true));
        await base.DisposeAsync();
    }

    private EtlDeployment Request(string script = "loadToCars({\n    Plate: this.Plate\n});\n") => new(
        Module,
        _targetDatabase,
        Store.Urls,
        [new EtlTransformScript("cars-v1-Cars", "Cars", script)]);

    private OngoingTaskRavenEtl? Task_()
        => Store.Maintenance.Send(new GetOngoingTaskInfoOperation($"spark-etl-{Module}", OngoingTaskType.RavenEtl)) as OngoingTaskRavenEtl;

    /// <summary>True when this server's licence refuses ETL, in which case there is nothing further to test.</summary>
    private static bool EtlIsNotLicensed(EtlDeploymentResult result)
        => !result.Success && result.Error == EtlDeploymentResult.NotLicensedError;

    private EtlTaskManager NewManager(IDocumentStore store) => new(store, _log, new RavenEtlLicence(store, NullLogger<RavenEtlLicence>.Instance));

    /// <summary>
    /// F18: the licence read reports what the server does. Where it says ETL is licensed the
    /// deployment goes through; where it says not, the deployment refuses with the reason instead
    /// of a generic failure. CI's Developer licence covers the first branch.
    /// </summary>
    [Fact]
    public async Task The_licence_read_agrees_with_what_the_server_does()
    {
        var hasEtl = await new RavenEtlLicence(Store, NullLogger<RavenEtlLicence>.Instance).HasRavenEtlAsync(CancellationToken.None);
        hasEtl.HasValue.Should().BeTrue("/license/status reports HasRavenEtl on every licence");

        var result = await NewManager(Store).DeployAsync(Request());

        if (hasEtl == true)
            result.Success.Should().BeTrue(result.Error ?? "");
        else
            result.Error.Should().Be(EtlDeploymentResult.NotLicensedError);
    }

    /// <summary>
    /// F16 at the database: the transform RavenDB holds is exactly the owner-generated script, and the
    /// connection string points at the deployment's (registry-taken) database.
    /// </summary>
    [Fact]
    public async Task The_deployed_transform_and_connection_string_are_the_owners()
    {
        var script = EtlTransformGenerator.Script([new EtlField("Plate", ["LicensePlate"])], "Cars");

        var result = await NewManager(Store).DeployAsync(Request(script));
        if (EtlIsNotLicensed(result))
            return;

        result.Success.Should().BeTrue(result.Error ?? "");
        var record = Store.Maintenance.Server.Send(new GetDatabaseRecordOperation(Store.Database));
        var etl = record.RavenEtls.Single(e => e.Name == $"spark-etl-{Module}");
        etl.Transforms.Single().Script.Should().Be(script);
        record.RavenConnectionStrings[$"spark-etl-{Module}"].Database.Should().Be(_targetDatabase);
    }

    [Fact]
    public async Task A_first_deployment_creates_the_task_and_a_second_updates_it()
    {
        var manager = NewManager(Store);

        var created = await manager.DeployAsync(Request());
        if (EtlIsNotLicensed(created))
            return;

        created.Success.Should().BeTrue(created.Error ?? "");
        created.TasksCreated.Should().Be(1);
        var task = Task_();
        task.Should().NotBeNull();

        var updated = await manager.DeployAsync(Request("loadToCars({\n    Plate: this.LicensePlate\n});\n"));

        updated.Success.Should().BeTrue(updated.Error ?? "");
        updated.TasksUpdated.Should().Be(1);
        updated.TasksCreated.Should().Be(0, "the task exists, so redeploying must not add a second one");

        // ⚠️ Not `TaskId.Should().Be(task.TaskId)`: RavenDB gives an ETL task a NEW id on every
        // update (the raft index of the update command), so an update and a delete-plus-add look
        // identical by id. What proves "updated, not duplicated" is one task under the name,
        // carrying the new script.
        var record = Store.Maintenance.Server.Send(new GetDatabaseRecordOperation(Store.Database));
        record.RavenEtls.Count(e => e.Name == task!.TaskName).Should().Be(1);
        record.RavenEtls.Single(e => e.Name == task!.TaskName).Transforms.Single().Script
            .Should().Contain("Plate: this.LicensePlate");
    }

    /// <summary>
    /// The bug: the lookup that decides "update or add" swallowed every exception and answered
    /// "no such task". A transient failure on that one request therefore sent an <b>add</b> for a
    /// task that already existed — a duplicate, which RavenDB refuses, so the deployment reported a
    /// failure whose logged cause was the duplicate rather than the fault that produced it.
    /// </summary>
    [Fact]
    public async Task A_failed_lookup_fails_the_deployment_instead_of_adding_a_duplicate()
    {
        // Where the licence allows it, make the task exist first: that is the case the bug turned
        // into a duplicate. Without ETL in the licence the assertion below still holds — a failed
        // lookup must not be answered by an add either way — so the test does not stop here.
        var created = await NewManager(Store).DeployAsync(Request());
        if (!EtlIsNotLicensed(created))
            created.Success.Should().BeTrue(created.Error ?? "");

        var requests = new ConcurrentQueue<string>();
        var lookupFailure = new InvalidOperationException("transient failure looking up the task");
        using var flaky = new DocumentStore { Urls = Store.Urls, Database = Store.Database };
        flaky.OnBeforeRequest += (_, e) =>
        {
            requests.Enqueue($"{e.Request.Method} {e.Url}");
            if (e.Url.Contains("/task?", StringComparison.Ordinal) || e.Url.Contains("/tasks?", StringComparison.Ordinal))
                throw lookupFailure;
        };
        flaky.Initialize();
        _log.Entries.Clear();

        // The licence was read above through the healthy store; the flaky one is only for the task lookup.
        var licence = Substitute.For<IRavenEtlLicence>();
        licence.HasRavenEtlAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(true));
        var result = await new EtlTaskManager(flaky, _log, licence).DeployAsync(Request());

        result.Success.Should().BeFalse();
        result.Error.Should().Be("ETL_DEPLOY_FAILED");
        requests.Any(r => r.Contains("/admin/etl", StringComparison.Ordinal) && !r.Contains("id=", StringComparison.Ordinal))
            .Should().BeFalse("an existing task must never be re-added because looking it up failed. Requests:\n{0}",
                string.Join("\n", requests));
        _log.Entries.Any(e => e.Level == LogLevel.Error && ContainsInChain(e.Exception, lookupFailure)).Should().BeTrue(
            "the logged cause must be the failure that happened");
    }

    private static bool ContainsInChain(Exception? exception, Exception target)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (ReferenceEquals(e, target))
                return true;
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(i => ContainsInChain(i, target)))
                return true;
        }
        return false;
    }
}
