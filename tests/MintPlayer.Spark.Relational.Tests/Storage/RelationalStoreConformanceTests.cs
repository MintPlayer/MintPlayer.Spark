using MintPlayer.Spark.Abstractions.Storage;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Relational.Schema;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Relational.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Storage;

namespace MintPlayer.Spark.Relational.Tests.Storage;

/// <summary>The store conformance suite against a relational provider: a database of its own per class.</summary>
public abstract class RelationalStoreConformanceTests : SparkStoreConformanceTests
{
    private RelationalTestDatabase? database;

    protected abstract RelationalTestServer? Server { get; }

    /// <summary>Without SPARK_TEST_SQLSERVER / SPARK_TEST_POSTGRESQL there is no store, and every case skips.</summary>
    protected override async Task<ISparkStore?> CreateStoreAsync()
    {
        if (Server is not { } server)
            return null;
        database = await RelationalTestDatabase.CreateAsync(server,
            [typeof(ConfDoc), typeof(ConfDocV1), typeof(ConfPerson), typeof(ConfCategory), typeof(ConfBox)]);
        return database.Store;
    }

    /// <summary>A SQL column keeps a <see cref="DateTime"/>'s ticks, not its kind (open question O18).</summary>
    protected override bool KeepsDateTimeKind => false;

    /// <summary>
    /// D16 on a table: a row written before a property existed reads it as the CLR default once the migration that
    /// adds its column ran. The V1 shape is mapped onto the same table, and the database moves from V1's model to V2's
    /// through the migration operations between them, as a generated migration would.
    /// </summary>
    [SkippableFact]
    public override async Task An_absent_field_reads_as_the_clr_default()
    {
        var server = Server ?? throw new SkipException("No server is configured for this provider.");
        var name = "spark_rel_" + Guid.NewGuid().ToString("N")[..12];
        await using var v1 = await RelationalTestDatabase.CreateAsync(
            server, [typeof(ConfDocV1)], mb => mb.Entity<ConfDocV1>().ToTable("ConfDocs"), name);
        using (var write = v1.Store.OpenSession())
        {
            await write.StoreAsync(new ConfDocV1 { Id = "ConfDocs/old", Name = "Old" });
            await write.SaveChangesAsync();
        }

        var v2 = RelationalTestDatabase.Open(server, [typeof(ConfDoc)], null, name);
        await using (var target = v2.NewContext())
        await using (var source = v1.Store.NewContext())
            await RelationalSchema.ApplyDifferencesAsync(target, source);

        using var session = v2.OpenSession();
        var loaded = await session.LoadAsync<ConfDoc>("ConfDocs/old");

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Old");
        loaded.Count.Should().Be(0);
        loaded.Flag.Should().BeFalse();
        loaded.Key.Should().Be(Guid.Empty);
        loaded.Status.Should().Be(ConfStatus.Draft);
        loaded.Tags.Should().BeEmpty();
    }

    public override async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }
}

/// <summary>The atomic-values suite against a relational provider's <c>SparkAtomicValues</c> table.</summary>
public abstract class RelationalAtomicValuesConformanceTests : SparkAtomicValuesConformanceTests
{
    private RelationalTestDatabase? database;

    protected abstract RelationalTestServer? Server { get; }

    protected override async Task<ISparkAtomicValues?> CreateAsync()
    {
        if (Server is not { } server)
            return null;
        database = await RelationalTestDatabase.CreateAsync(server, []);
        return new RelationalAtomicValues(database.Store);
    }

    public override async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }
}

/// <summary>The subscriptions suite against a relational provider's commit stamps (#536 D39).</summary>
public abstract class RelationalSubscriptionsConformanceTests : SparkSubscriptionsConformanceTests
{
    private RelationalTestDatabase? database;

    protected abstract RelationalTestServer? Server { get; }

    protected override async Task<(ISparkStore Store, ISparkSubscriptions Subscriptions)?> CreateAsync()
    {
        if (Server is not { } server)
            return null;
        database = await RelationalTestDatabase.CreateAsync(server, [typeof(SubDoc)]);
        var values = new RelationalAtomicValues(database.Store);
        var subscriptions = new RelationalSparkSubscriptions(database.Store, values,
            new MintPlayer.Spark.Services.SparkStoreLock(values, TimeProvider.System), pollInterval: TimeSpan.FromMilliseconds(200));
        return (database.Store, subscriptions);
    }

    public override async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }
}

/// <summary>The revisions suite against a relational provider's <c>spark.Revisions</c> table (#536 S8).</summary>
public abstract class RelationalRevisionsConformanceTests : SparkRevisionsConformanceTests
{
    private RelationalTestDatabase? database;

    protected abstract RelationalTestServer? Server { get; }

    protected override async Task<(ISparkStore Store, ISparkRevisions Revisions)?> CreateAsync()
    {
        if (Server is not { } server)
            return null;
        database = await RelationalTestDatabase.CreateAsync(server, [typeof(RevDoc), typeof(RevOther)]);
        return (database.Store, new RelationalSparkRevisions(database.Store));
    }

    public override async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }
}

public sealed class SqlServerRevisionsConformanceTests : RelationalRevisionsConformanceTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlRevisionsConformanceTests : RelationalRevisionsConformanceTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}

public sealed class SqlServerSubscriptionsConformanceTests : RelationalSubscriptionsConformanceTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlSubscriptionsConformanceTests : RelationalSubscriptionsConformanceTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;

    /// <summary>PostgreSQL stamps a whole transaction with one xid: a batch never cuts through it, whatever its size.</summary>
    [SkippableFact]
    public async Task One_transaction_is_never_split_across_batches()
    {
        await StoreAsync(Doc("subdocs/t1"), Doc("subdocs/t2"), Doc("subdocs/t3"), Doc("subdocs/t4"), Doc("subdocs/t5"));

        await using var runner = new Runner(Subscriptions, new SparkSubscriptionDefinition<SubDoc>("conf-xid"), Fast);
        await runner.WaitForAsync("subdocs/t1", "subdocs/t2", "subdocs/t3", "subdocs/t4", "subdocs/t5");

        runner.Batches.Should().ContainSingle().Which.Should().HaveCount(5);
    }
}

public sealed class SqlServerAtomicValuesConformanceTests : RelationalAtomicValuesConformanceTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlAtomicValuesConformanceTests : RelationalAtomicValuesConformanceTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}

public sealed class SqlServerStoreConformanceTests : RelationalStoreConformanceTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlStoreConformanceTests : RelationalStoreConformanceTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}
