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
/// <see cref="ModuleInformation"/> on first registration, and updating it in-place
/// on re-registration.
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

    /// <summary>
    /// The connection to the shared SparkModules database now belongs to
    /// <see cref="ModuleDirectory"/>, which caches one per process. Each call here gets its own
    /// so the tests stay isolated from each other.
    /// </summary>
    private ModuleDirectory NewDirectory(SparkReplicationOptions? opts = null)
        => new(Options.Create(opts ?? DefaultOptions()));

    [Fact]
    public void The_module_directory_connects_to_the_configured_SparkModules_database()
    {
        using var directory = NewDirectory();

        directory.Store.Database.Should().Be(_modulesDatabase);
        directory.Store.Urls.Should().BeEquivalentTo(Store.Urls);
    }

    /// <summary>
    /// One store for the process, not one per lookup. Before this, every mTLS validation
    /// constructed and initialized a fresh <c>DocumentStore</c> (F6) — meaning unauthenticated
    /// inbound requests drove store creation and teardown.
    /// </summary>
    [Fact]
    public void The_module_directory_reuses_a_single_store()
    {
        using var directory = NewDirectory();

        directory.Store.Should().BeSameAs(directory.Store);
    }

    /// <summary>
    /// "Not registered" and "registered" have to be distinguishable, because that distinction is
    /// the whole of the Development-mode authentication check (F1).
    /// </summary>
    [Fact]
    public async Task The_module_directory_returns_null_for_a_module_that_never_registered()
    {
        var opts = DefaultOptions(moduleName: "HR");
        using var directory = NewDirectory(opts);
        await NewService(opts).RegisterAsync(directory.Store);

        (await directory.FindAsync("HR", CancellationToken.None)).Should().NotBeNull();
        (await directory.FindAsync("Never-Registered", CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// An empty name resolves to "no such module" rather than to an argument exception, so no
    /// caller has to guard for it separately — the answer is the same refusal either way.
    /// Deliberately checked before the registry is reachable: this must not depend on it.
    /// </summary>
    [Fact]
    public async Task The_module_directory_treats_an_empty_name_as_no_such_module()
    {
        using var directory = NewDirectory();

        (await directory.FindAsync("", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_creates_the_SparkModules_database_when_missing_and_stores_module_info()
    {
        var opts = DefaultOptions(moduleName: "HR");
        var service = NewService(opts);
        using var directory = NewDirectory(opts);
        var modulesStore = directory.Store;

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
        using (var directory = NewDirectory(initialOpts))
        {
            await NewService(initialOpts).RegisterAsync(directory.Store);
        }

        // Restart with a rotated URL — should overwrite the existing document.
        var rotatedOpts = DefaultOptions(moduleName: "Fleet", moduleUrl: "http://fleet.internal:8080");
        using (var directory = NewDirectory(rotatedOpts))
        {
            await NewService(rotatedOpts).RegisterAsync(directory.Store);
        }

        // Verify the document carries the rotated URL, no duplicate documents — read through
        // the same derived-id lookup the runtime uses, not a hand-written id string.
        using var verify = NewDirectory();
        var info = await verify.FindAsync("Fleet", CancellationToken.None);
        info.Should().NotBeNull();
        info!.AppUrl.Should().Be("http://fleet.internal:8080");
    }
}
