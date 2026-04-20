using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Replication.Workers;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// End-to-end tests for <see cref="SyncActionSubscriptionWorker"/> driven by a real
/// RavenDB subscription. Owner module resolution goes through a co-located
/// <c>SparkModulesTest</c> database on the embedded test server, and the outbound
/// HTTP call is intercepted by <see cref="StubHttpMessageHandler"/>.
/// </summary>
public class SyncActionSubscriptionWorkerE2ETests : SparkTestDriver
{
    private readonly string _modulesDatabase = $"SparkModulesTest-{Guid.NewGuid():N}";
    private const string OwnerModule = "Fleet";
    private const string RequestingModule = "HR";
    private const string OwnerUrl = "http://fleet.test";

    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(20);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Co-locate a SparkModules database on the same embedded server so
        // ModuleRegistrationService.CreateModulesStore() reaches something real.
        // Each test instance uses a unique DB name because RavenTestDriver reuses
        // one embedded server across tests.
        Store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(_modulesDatabase)));

        using var modulesStore = CreateModulesStore();
        using (var session = modulesStore.OpenAsyncSession())
        {
            var documentId = $"moduleInformations/{OwnerModule}";
            await session.StoreAsync(new ModuleInformation
            {
                Id = documentId,
                AppName = OwnerModule,
                AppUrl = OwnerUrl,
                DatabaseName = "fleet-db",
                RegisteredAtUtc = DateTime.UtcNow,
            }, documentId);
            await session.SaveChangesAsync();
        }
    }

    private IDocumentStore CreateModulesStore()
    {
        var store = new DocumentStore { Urls = Store.Urls, Database = _modulesDatabase };
        store.Initialize();
        return store;
    }

    // --- Helpers --------------------------------------------------------------

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ =>
            new HttpResponseMessage(HttpStatusCode.OK);

        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastRequestBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return Respond(request);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHttpMessageHandler _handler;
        public StubHttpClientFactory(StubHttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private SparkReplicationOptions DefaultOptions() => new()
    {
        ModuleName = RequestingModule,
        ModuleUrl = "http://hr.test",
        SparkModulesUrls = Store.Urls,
        SparkModulesDatabase = _modulesDatabase,
    };

    private SyncActionSubscriptionWorker NewWorker(StubHttpMessageHandler handler, SparkReplicationOptions? opts = null)
    {
        var options = opts ?? DefaultOptions();
        var registration = new ModuleRegistrationService(
            Options.Create(options),
            Store,
            NullLogger<ModuleRegistrationService>.Instance);

        return new SyncActionSubscriptionWorker(
            Store,
            new StubHttpClientFactory(handler),
            registration,
            NullLogger<SyncActionSubscriptionWorker>.Instance);
    }

    private async Task<string> SeedSyncActionAsync(string ownerModuleName = OwnerModule)
    {
        var doc = new SparkSyncAction
        {
            OwnerModuleName = ownerModuleName,
            RequestingModule = RequestingModule,
            Collection = "Cars",
            Actions = [new SyncAction
            {
                ActionType = SyncActionType.Insert,
                Collection = "Cars",
                Data = new Dictionary<string, object?> { ["Plate"] = "ABC-123" },
            }],
        };

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(doc);
            await session.SaveChangesAsync();
        }
        WaitForIndexing(Store);
        return doc.Id!;
    }

    private async Task<SparkSyncAction> WaitForAsync(string id, Func<SparkSyncAction, bool> predicate, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? PollTimeout);
        SparkSyncAction? last = null;
        while (DateTime.UtcNow < end)
        {
            using var session = Store.OpenAsyncSession();
            last = await session.LoadAsync<SparkSyncAction>(id);
            if (last != null && predicate(last))
                return last;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Predicate for SparkSyncAction '{id}' not met within {timeout ?? PollTimeout}. Last: Status={last?.Status}, LastError={last?.LastError}");
    }

    private async Task<long?> GetRetryCounterAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.CountersFor(id).GetAsync("SparkRetryAttempts");
    }

    // --- Tests ----------------------------------------------------------------

    [Fact]
    public async Task Happy_path_200_response_marks_syncAction_Completed_and_POSTs_to_owner_apply_endpoint()
    {
        var handler = new StubHttpMessageHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) };
        var id = await SeedSyncActionAsync();
        var worker = NewWorker(handler);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForAsync(id, s => s.Status == ESyncActionStatus.Completed);

            final.LastError.Should().BeNull();
            final.NextAttemptAtUtc.Should().BeNull("happy path clears any previously scheduled retry window");
            handler.CallCount.Should().Be(1);
            handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
            handler.LastRequest.RequestUri!.ToString().Should().Be($"{OwnerUrl}/spark/sync/apply");
            handler.LastRequestBody.Should().Contain("\"requestingModule\":\"HR\"");
            handler.LastRequestBody.Should().Contain("\"actionType\":0"); // Insert
            (await GetRetryCounterAsync(id)).Should().BeNull("retry counter should be cleared on success");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BadRequest_400_marks_syncAction_Failed_as_non_retryable_and_does_not_touch_retry_counter()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("validation failed", Encoding.UTF8, "text/plain"),
            },
        };

        var id = await SeedSyncActionAsync();
        var worker = NewWorker(handler);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForAsync(id, s => s.Status == ESyncActionStatus.Failed);
            final.LastError.Should().Contain("BadRequest").And.Contain("validation failed");
            (await GetRetryCounterAsync(id)).Should().BeNull();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NotFound_404_marks_syncAction_Failed_as_non_retryable()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("unknown endpoint"),
            },
        };

        var id = await SeedSyncActionAsync();
        var worker = NewWorker(handler);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForAsync(id, s => s.Status == ESyncActionStatus.Failed);
            final.LastError.Should().Contain("NotFound");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ServerError_500_schedules_retry_via_RetryNumerator_with_NextAttemptAtUtc_gating_redelivery()
    {
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            },
        };

        var id = await SeedSyncActionAsync();
        var worker = NewWorker(handler);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForAsync(id, s => s.Status == ESyncActionStatus.Pending && s.LastError != null);

            final.LastError.Should().Contain("InternalServerError").And.Contain("boom");
            final.NextAttemptAtUtc.Should().NotBeNull("the subscription query gates re-delivery on this field");
            final.NextAttemptAtUtc!.Value.Should().BeAfter(DateTime.UtcNow, "backoff schedules the next attempt into the future");

            // Give the subscription a moment to redeliver; NextAttemptAtUtc should keep the
            // document out of the query, so the handler stays at a single call.
            await Task.Delay(500);
            handler.CallCount.Should().Be(1, "NextAttemptAtUtc prevents change-vector-driven immediate re-delivery");
            (await GetRetryCounterAsync(id)).Should().Be(1);

            using var session = Store.OpenAsyncSession();
            var doc = await session.LoadAsync<SparkSyncAction>(id);
            var metadata = session.Advanced.GetMetadataFor(doc);
            metadata.ContainsKey("@refresh").Should().BeTrue("RetryNumerator still writes @refresh for Raven's Refresh feature");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Unknown_owner_module_schedules_retry_and_never_hits_the_http_handler()
    {
        // ResolveModuleUrlAsync throws before any HTTP call can be made.
        var handler = new StubHttpMessageHandler();
        var id = await SeedSyncActionAsync(ownerModuleName: "GhostModule");
        var worker = NewWorker(handler);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var final = await WaitForAsync(id, s => s.Status == ESyncActionStatus.Pending && s.LastError != null);

            final.LastError.Should().Contain("GhostModule").And.Contain("not found");
            final.NextAttemptAtUtc.Should().NotBeNull();
            handler.CallCount.Should().Be(0);

            (await GetRetryCounterAsync(id)).Should().Be(1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
