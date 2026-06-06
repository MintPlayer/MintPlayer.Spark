using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Messages;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Pins the contract introduced by issue #148: the recipient resolves the source
/// module's URL from <c>SparkModules</c> on every delivery, never trusts a URL
/// baked into the message at send time. A retry after the source module finally
/// registers (or rotates URL) hits the freshly-loaded value.
/// </summary>
public class EtlScriptDeploymentRecipientTests : SparkTestDriver
{
    private readonly string _modulesDatabase = $"SparkModulesTest-{Guid.NewGuid():N}";
    private const string SourceModule = "Fleet";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(_modulesDatabase)));
    }

    private async Task SeedSourceAsync(string moduleName, string appUrl)
    {
        using var modulesStore = new DocumentStore { Urls = Store.Urls, Database = _modulesDatabase };
        modulesStore.Initialize();

        using var session = modulesStore.OpenAsyncSession();
        var documentId = $"moduleInformations/{moduleName}";
        var existing = await session.LoadAsync<ModuleInformation>(documentId);
        if (existing != null)
        {
            existing.AppUrl = appUrl;
            existing.RegisteredAtUtc = DateTime.UtcNow;
        }
        else
        {
            await session.StoreAsync(new ModuleInformation
            {
                Id = documentId,
                AppName = moduleName,
                AppUrl = appUrl,
                DatabaseName = "fleet-db",
                RegisteredAtUtc = DateTime.UtcNow,
            }, documentId);
        }
        await session.SaveChangesAsync();
    }

    private SparkReplicationOptions DefaultOptions() => new()
    {
        ModuleName = "HR",
        ModuleUrl = "http://hr.test",
        SparkModulesUrls = Store.Urls,
        SparkModulesDatabase = _modulesDatabase,
    };

    private EtlScriptDeploymentRecipient NewRecipient(StubHttpMessageHandler handler)
    {
        var registration = new ModuleRegistrationService(
            Options.Create(DefaultOptions()),
            Store,
            NullLogger<ModuleRegistrationService>.Instance);
        return new EtlScriptDeploymentRecipient(
            new StubHttpClientFactory(handler),
            registration,
            NullLogger<EtlScriptDeploymentRecipient>.Instance);
    }

    private static EtlScriptDeploymentMessage Msg(string source = SourceModule) => new()
    {
        SourceModuleName = source,
        Request = new EtlScriptRequest
        {
            RequestingModule = "HR",
            TargetDatabase = "hr-db",
            TargetUrls = ["http://localhost:8080"],
            Scripts = [],
        },
    };

    [Fact]
    public async Task Resolves_url_from_SparkModules_and_POSTs_to_it()
    {
        await SeedSourceAsync(SourceModule, "http://fleet.test:9090");
        var handler = new StubHttpMessageHandler();
        var recipient = NewRecipient(handler);

        await recipient.HandleAsync(Msg(), CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://fleet.test:9090/spark/etl/deploy");
    }

    [Fact]
    public async Task Throws_InvalidOperationException_when_source_module_is_not_registered()
    {
        // Modules database exists but contains no entry for SourceModule.
        var handler = new StubHttpMessageHandler();
        var recipient = NewRecipient(handler);

        var act = () => recipient.HandleAsync(Msg(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains(SourceModule) && e.Message.Contains("not registered"));
        handler.CallCount.Should().Be(0, "HTTP must not be attempted when the source can't be resolved");
    }

    [Fact]
    public async Task Throws_when_source_module_has_empty_AppUrl()
    {
        await SeedSourceAsync(SourceModule, "");
        var handler = new StubHttpMessageHandler();
        var recipient = NewRecipient(handler);

        var act = () => recipient.HandleAsync(Msg(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Trims_trailing_slash_from_source_url_before_appending_endpoint_path()
    {
        await SeedSourceAsync(SourceModule, "http://fleet.test/");
        var handler = new StubHttpMessageHandler();
        var recipient = NewRecipient(handler);

        await recipient.HandleAsync(Msg(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://fleet.test/spark/etl/deploy");
    }

    [Fact]
    public async Task Throws_HttpRequestException_when_source_returns_non_success()
    {
        await SeedSourceAsync(SourceModule, "http://fleet.test");
        var handler = new StubHttpMessageHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            },
        };
        var recipient = NewRecipient(handler);

        var act = () => recipient.HandleAsync(Msg(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>()
            .Where(e => e.Message.Contains(SourceModule) && e.Message.Contains("boom"));
    }

    [Fact]
    public async Task Picks_up_a_changed_source_url_on_a_subsequent_delivery_attempt()
    {
        // The bug fix: previously, the URL was baked into the message at send time, so a
        // retry after the source rotated its URL would still hit the stale value. Now the
        // recipient re-reads SparkModules on every delivery → the second call uses the
        // updated AppUrl.
        await SeedSourceAsync(SourceModule, "http://fleet.test:5000");
        var handler = new StubHttpMessageHandler();
        var recipient = NewRecipient(handler);

        await recipient.HandleAsync(Msg(), CancellationToken.None);
        handler.LastRequest!.RequestUri!.ToString().Should().Be("http://fleet.test:5000/spark/etl/deploy");

        // Source rotates URL (port + host).
        await SeedSourceAsync(SourceModule, "http://fleet.internal:8080");

        await recipient.HandleAsync(Msg(), CancellationToken.None);
        handler.LastRequest.RequestUri!.ToString().Should().Be("http://fleet.internal:8080/spark/etl/deploy");
    }

    // ---- helpers --------------------------------------------------------

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHttpMessageHandler _handler;
        public StubHttpClientFactory(StubHttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
