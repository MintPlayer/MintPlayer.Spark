using MintPlayer.Spark.Abstractions.Storage;
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
    /// D16 on a table: a column a migration added reads as the CLR default in rows written before it. That needs the
    /// migrations of #536 M4c; a type of another shape is another table here, not the same document.
    /// </summary>
    [SkippableFact]
    public override Task An_absent_field_reads_as_the_clr_default()
    {
        Skip.If(true, "#536 M4c: the relational form of D16 is a column added by a migration.");
        return Task.CompletedTask;
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
