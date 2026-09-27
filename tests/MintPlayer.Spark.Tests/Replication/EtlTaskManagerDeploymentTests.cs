using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.OngoingTasks;
using Raven.Client.Exceptions.Commercial;
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

    private EtlScriptRequest Request(string script = "loadToCars(this);") => new()
    {
        RequestingModule = Module,
        TargetDatabase = _targetDatabase,
        TargetUrls = Store.Urls,
        Scripts = [new EtlScriptItem { SourceCollection = "Cars", Script = script }],
    };

    private OngoingTaskRavenEtl? Task_()
        => Store.Maintenance.Send(new GetOngoingTaskInfoOperation($"spark-etl-{Module}", OngoingTaskType.RavenEtl)) as OngoingTaskRavenEtl;

    /// <summary>True when this server's licence refuses ETL, in which case there is nothing further to test.</summary>
    private bool EtlIsNotLicensed(EtlDeploymentResult result)
        => !result.Success && _log.Entries.Any(e => e.Exception is LicenseLimitException);

    [Fact]
    public async Task A_first_deployment_creates_the_task_and_a_second_updates_it()
    {
        var manager = new EtlTaskManager(Store, _log);

        var created = await manager.DeployAsync(Request());
        if (EtlIsNotLicensed(created))
            return;

        created.Success.Should().BeTrue(created.Error ?? "");
        created.TasksCreated.Should().Be(1);
        var task = Task_();
        task.Should().NotBeNull();

        var updated = await manager.DeployAsync(Request("loadToCars({ Plate: this.Plate });"));

        updated.Success.Should().BeTrue(updated.Error ?? "");
        updated.TasksUpdated.Should().Be(1);
        updated.TasksCreated.Should().Be(0, "the task exists, so redeploying must not add a second one");
        Task_()!.TaskId.Should().Be(task!.TaskId);
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
        var created = await new EtlTaskManager(Store, _log).DeployAsync(Request());
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

        var result = await new EtlTaskManager(flaky, _log).DeployAsync(Request());

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
