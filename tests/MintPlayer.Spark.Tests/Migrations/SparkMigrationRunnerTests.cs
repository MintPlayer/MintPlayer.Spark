using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Migrations;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
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

    private const string LockKey = "spark/migrations/lock";

    private ServiceProvider BuildEmptyProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddScoped<IAsyncDocumentSession>(_ => Store.OpenAsyncSession());
        services.AddSingleton(_recorder);
        services.AddSingleton(new SparkMigrationRegistry());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task No_migrations_registered_is_a_noop()
    {
        var sp = BuildEmptyProvider();

        await SparkMigrationRunner.RunAsync(sp, CancellationToken.None);

        _recorder.Applied.Should().BeEmpty();
        // No lock was ever taken (early return before acquiring it).
        var current = await Store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DateTime>(LockKey));
        current.Should().BeNull();
    }

    [Fact]
    public async Task RunAtStartup_applies_migrations_synchronously()
    {
        var sp = BuildProvider(typeof(Mig_1), typeof(Mig_2));

        // Synchronous wrapper: blocks until done.
        SparkMigrationRunner.RunAtStartup(sp);

        _recorder.Applied.Should().Equal(202601010001, 202601010002);

        using var session = Store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010001))).Should().BeTrue();
    }

    [Fact]
    public async Task Lock_is_released_after_success_so_a_later_run_proceeds()
    {
        // First run applies Mig_1 + Mig_2 and must RELEASE the lock in its finally block.
        var first = BuildProvider(typeof(Mig_1), typeof(Mig_2));
        await SparkMigrationRunner.RunAsync(first, CancellationToken.None);

        // The lock is gone — a later run can acquire it fresh.
        var afterFirst = await Store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DateTime>(LockKey));
        afterFirst.Should().BeNull("the runner releases the compare-exchange lock after a successful run");

        // Second run adds a NEW higher-version migration; it must acquire the lock again and apply it.
        var second = BuildProvider(typeof(Mig_1), typeof(Mig_2), typeof(Mig_3));
        await SparkMigrationRunner.RunAsync(second, CancellationToken.None);

        _recorder.Applied.Should().Equal(202601010001, 202601010002, 202601010003);

        using var session = Store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010003)))
            .Should().BeTrue("the later run re-acquired the released lock and applied the new migration");
    }

    /// <summary>
    /// ⚠️ This used to assert the runner <b>skipped</b> when another instance held the lock — and
    /// skipping meant the caller went on to serve.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-21 by killing a container midway through a 224,000-document re-key: the
    /// compare-exchange lock outlives the process, because a killed process runs no <c>finally</c>.
    /// Every restart for the next 30 minutes then skipped the migration and served a
    /// <b>half-migrated database</b>, silently. A migration that <em>throws</em> releases the lock
    /// and retries, which is what made skipping look safe.
    /// <para>
    /// A node that cannot take the lock has, by definition, migrations that are not applied yet, so
    /// there is no state in which serving is correct. It waits, and fails loudly if the wait is
    /// hopeless.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Run_refuses_to_proceed_while_another_instance_holds_the_lock()
    {
        // Pre-seed a live (future-dated) lock as if another node holds it.
        await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<DateTime>(
            LockKey, DateTime.UtcNow.AddMinutes(20), 0));

        var sp = BuildProvider(typeof(Mig_1), typeof(Mig_2));

        var act = async () => await SparkMigrationRunner.RunAsync(
            sp, CancellationToken.None, lockWaitBudget: TimeSpan.FromSeconds(2));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*serving now would serve un-migrated data*");

        _recorder.Applied.Should().BeEmpty("the lock holder is the one applying them");

        using var session = Store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010001)))
            .Should().BeFalse();
    }

    /// <summary>
    /// The legitimate multi-instance case: the holder finishes while this node waits, so there is
    /// nothing left to apply and startup continues normally.
    /// </summary>
    [Fact]
    public async Task Run_continues_once_the_holder_has_applied_everything()
    {
        await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<DateTime>(
            LockKey, DateTime.UtcNow.AddMinutes(20), 0));

        // Markers for both migrations, as the holder would have written them.
        using (var seed = Store.OpenAsyncSession())
        {
            foreach (var version in new[] { 202601010001L, 202601010002L })
            {
                await seed.StoreAsync(
                    new SparkMigrationRecord { Version = version, Name = "x", AppliedOnUtc = DateTimeOffset.UtcNow },
                    SparkMigrationRunner.MarkerId(version));
            }

            await seed.SaveChangesAsync();
        }

        var sp = BuildProvider(typeof(Mig_1), typeof(Mig_2));

        await SparkMigrationRunner.RunAsync(sp, CancellationToken.None, lockWaitBudget: TimeSpan.FromSeconds(5));

        _recorder.Applied.Should().BeEmpty("they were already applied by the instance holding the lock");
    }

    [Fact]
    public async Task Run_takes_over_an_expired_lock()
    {
        // Pre-seed an EXPIRED lock (a crashed prior run). The runner must take it over and proceed.
        await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<DateTime>(
            LockKey, DateTime.UtcNow.AddMinutes(-5), 0));

        var sp = BuildProvider(typeof(Mig_1), typeof(Mig_2));

        await SparkMigrationRunner.RunAsync(sp, CancellationToken.None);

        _recorder.Applied.Should().Equal(202601010001, 202601010002);

        using var session = Store.OpenAsyncSession();
        (await session.Advanced.ExistsAsync(SparkMigrationRunner.MarkerId(202601010001)))
            .Should().BeTrue("an expired lease is taken over so the run proceeds");
    }
}
