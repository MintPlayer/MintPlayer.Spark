using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// Integration coverage for <see cref="SparkMigrationRunner"/> against embedded RavenDB:
/// ordered run, per-version marker documents (idempotent across runs), and fail-fast.
/// </summary>
public class SparkMigrationRunnerTests : SparkTestDriver
{
    // Shared observer so we can see what ran, across DI scopes.
    public sealed class Recorder { public List<long> Applied { get; } = []; }

    public sealed class Mig_1 : ISparkMigration
    {
        public static long Version => 202601010001;
        private readonly Recorder _recorder; private readonly IAsyncDocumentSession _session;
        public Mig_1(Recorder recorder, IAsyncDocumentSession session) { _recorder = recorder; _session = session; }
        public async Task UpAsync(CancellationToken ct)
        {
            _recorder.Applied.Add(Version);
            await _session.StoreAsync(new SeededDoc { Tag = "one" }, "seeded/1", ct);
            await _session.SaveChangesAsync(ct);
        }
    }

    public sealed class Mig_2 : ISparkMigration
    {
        public static long Version => 202601010002;
        private readonly Recorder _recorder;
        public Mig_2(Recorder recorder) { _recorder = recorder; }
        public Task UpAsync(CancellationToken ct) { _recorder.Applied.Add(Version); return Task.CompletedTask; }
    }

    public sealed class Mig_3 : ISparkMigration
    {
        public static long Version => 202601010003;
        private readonly Recorder _recorder;
        public Mig_3(Recorder recorder) { _recorder = recorder; }
        public Task UpAsync(CancellationToken ct) { _recorder.Applied.Add(Version); return Task.CompletedTask; }
    }

    // A migration that throws — registered with a version BETWEEN 1 and 3 to prove fail-fast halts.
    public sealed class Mig_Boom : ISparkMigration
    {
        public static long Version => 202601010002; // same slot as Mig_2 when used alone
        public Task UpAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    public sealed class SeededDoc { public string? Id { get; set; } public string Tag { get; set; } = ""; }

    private readonly Recorder _recorder = new();

    private ServiceProvider BuildProvider(params Type[] migrationTypes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddScoped<IAsyncDocumentSession>(_ => Store.OpenAsyncSession());
        services.AddSingleton(_recorder);

        var registry = new SparkMigrationRegistry();
        var builder = new SparkMigrationsBuilder(services, registry);
        foreach (var t in migrationTypes)
        {
            services.AddScoped(t);
            // Register descriptor via the generic AddMigration<T> using reflection over the test types.
            typeof(SparkMigrationsBuilder).GetMethod(nameof(ISparkMigrationsBuilder.AddMigration))!
                .MakeGenericMethod(t).Invoke(builder, null);
        }
        services.AddSingleton(registry);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Runs_pending_migrations_in_version_order_and_writes_markers()
    {
        // Register out of order to prove the runner sorts by version.
        var sp = BuildProvider(typeof(Mig_3), typeof(Mig_1), typeof(Mig_2));

        await SparkMigrationRunner.RunAsync(sp, CancellationToken.None);

        _recorder.Applied.Should().Equal(202601010001, 202601010002, 202601010003);

        using var session = Store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010001))).Should().BeTrue();
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010003))).Should().BeTrue();
        (await session.Advanced.ExistsAsync("seeded/1")).Should().BeTrue("the migration's own writes were persisted");
    }

    [Fact]
    public async Task Applied_migrations_are_skipped_on_a_second_run()
    {
        var sp = BuildProvider(typeof(Mig_1), typeof(Mig_2));

        await SparkMigrationRunner.RunAsync(sp, CancellationToken.None);
        await SparkMigrationRunner.RunAsync(sp, CancellationToken.None);

        _recorder.Applied.Should().Equal(202601010001, 202601010002);
    }

    [Fact]
    public async Task A_throwing_migration_halts_the_run_and_leaves_itself_unmarked()
    {
        // Mig_1 (applies) → Mig_Boom (throws, version 2) → Mig_3 (must NOT run).
        var sp = BuildProvider(typeof(Mig_1), typeof(Mig_Boom), typeof(Mig_3));

        var act = async () => await SparkMigrationRunner.RunAsync(sp, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        // Mig_1 ran; Mig_3 must not — fail-fast halts the run at the throwing migration.
        _recorder.Applied.Should().Equal(202601010001L);

        using var session = Store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010001))).Should().BeTrue();
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010002)))
            .Should().BeFalse("a migration that threw must not be marked applied");
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010003))).Should().BeFalse();
    }
}
