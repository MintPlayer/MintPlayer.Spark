using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace MintPlayer.Spark.Tests.Replication;

/// <summary>
/// Pins <see cref="ModuleRegistrationService"/> against a real RavenDB instance:
/// auto-creating the SparkModules database when missing, storing a fresh
/// <see cref="ModuleInformation"/> on first registration, updating it in-place
/// on re-registration, and exposing a connected <c>SparkModules</c> store via
/// <see cref="ModuleRegistrationService.CreateModulesStore"/>.
/// </summary>
public class ModuleRegistrationServiceTests : SparkTestDriver
{
    private readonly string _modulesDatabase = $"SparkModulesTest-{Guid.NewGuid():N}";

    private SparkReplicationOptions DefaultOptions(string moduleName = "HR", string moduleUrl = "http://hr.test:8080") => new()
    {
        ModuleName = moduleName,
        ModuleUrl = moduleUrl,
        SparkModulesUrls = Store.Urls,
        SparkModulesDatabase = _modulesDatabase,
    };

    private ModuleRegistrationService NewService(SparkReplicationOptions? opts = null)
        => new(Options.Create(opts ?? DefaultOptions()), Store, NullLogger<ModuleRegistrationService>.Instance);

    [Fact]
    public void CreateModulesStore_returns_an_initialized_store_pointing_at_the_configured_database()
    {
        var service = NewService();

        using var modulesStore = service.CreateModulesStore();

        modulesStore.Should().NotBeNull();
        modulesStore.Database.Should().Be(_modulesDatabase);
        modulesStore.Urls.Should().BeEquivalentTo(Store.Urls);
    }

    [Fact]
    public async Task RegisterAsync_creates_the_SparkModules_database_when_missing_and_stores_module_info()
    {
        var service = NewService(DefaultOptions(moduleName: "HR"));
        using var modulesStore = service.CreateModulesStore();

        await service.RegisterAsync(modulesStore);

        // The SparkModules database should now exist on the embedded server.
        var dbs = Store.Maintenance.Server.Send(new GetDatabaseNamesOperation(0, int.MaxValue));
        dbs.Should().Contain(_modulesDatabase);

        // And carry a freshly-stored ModuleInformation document.
        using var session = modulesStore.OpenAsyncSession();
        var info = await session.LoadAsync<ModuleInformation>("moduleInformations/HR");
        info.Should().NotBeNull();
        info!.AppName.Should().Be("HR");
        info.AppUrl.Should().Be("http://hr.test:8080");
        info.DatabaseName.Should().Be(Store.Database);
        info.RegisteredAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RegisterAsync_updates_in_place_on_re_registration_with_a_changed_url()
    {
        var initialOpts = DefaultOptions(moduleName: "Fleet", moduleUrl: "http://fleet.test:5000");
        var initialService = NewService(initialOpts);
        using (var modulesStore = initialService.CreateModulesStore())
        {
            await initialService.RegisterAsync(modulesStore);
        }

        // Restart with a rotated URL — should overwrite the existing document.
        var rotatedOpts = DefaultOptions(moduleName: "Fleet", moduleUrl: "http://fleet.internal:8080");
        var rotatedService = NewService(rotatedOpts);
        using (var modulesStore = rotatedService.CreateModulesStore())
        {
            await rotatedService.RegisterAsync(modulesStore);
        }

        // Verify the document carries the rotated URL, no duplicate documents.
        using var verify = NewService().CreateModulesStore();
        using var session = verify.OpenAsyncSession();
        var info = await session.LoadAsync<ModuleInformation>("moduleInformations/Fleet");
        info.Should().NotBeNull();
        info!.AppUrl.Should().Be("http://fleet.internal:8080");
    }
}
