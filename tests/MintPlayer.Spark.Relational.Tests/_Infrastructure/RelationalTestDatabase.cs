using Microsoft.Data.SqlClient;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Storage;
using Npgsql;

namespace MintPlayer.Spark.Relational.Tests._Infrastructure;

/// <summary>A database server the tests can use: its dialect, and the connection string for a database on it.</summary>
public sealed class RelationalTestServer
{
    private readonly string serverConnectionString;

    private RelationalTestServer(string name, SparkRelationalDialect dialect, string serverConnectionString)
    {
        Name = name;
        Dialect = dialect;
        this.serverConnectionString = serverConnectionString;
    }

    public string Name { get; }

    public SparkRelationalDialect Dialect { get; }

    public static RelationalTestServer? SqlServer
        => Environment.GetEnvironmentVariable("SPARK_TEST_SQLSERVER") is { Length: > 0 } cs
            ? new("SQL Server", new SqlServerSparkDialect(), cs)
            : null;

    public static RelationalTestServer? PostgreSql
        => Environment.GetEnvironmentVariable("SPARK_TEST_POSTGRESQL") is { Length: > 0 } cs
            ? new("PostgreSQL", new PostgreSqlSparkDialect(), cs)
            : null;

    public string ConnectionStringFor(string database) => Dialect is SqlServerSparkDialect
        ? new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = database }.ConnectionString
        : new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = database }.ConnectionString;
}

/// <summary>
/// A database of its own for one test class: created with the schema of <paramref name="types"/>, dropped afterwards.
/// </summary>
public sealed class RelationalTestDatabase : IAsyncDisposable
{
    private RelationalTestDatabase(RelationalSparkStore store) => Store = store;

    public RelationalSparkStore Store { get; }

    public static async Task<RelationalTestDatabase> CreateAsync(RelationalTestServer server, IEnumerable<Type> types)
    {
        var name = "spark_rel_" + Guid.NewGuid().ToString("N")[..12];
        var model = SparkRelationalModel.Build(types, t => SparkRelationalExtensions.KindOf(t, new SparkIdOptions()));
        var store = new RelationalSparkStore(new SparkContextSetup(model, server.Dialect, []), server.ConnectionStringFor(name));
        await using var db = store.NewContext();
        await db.Database.EnsureCreatedAsync();
        return new RelationalTestDatabase(store);
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = Store.NewContext();
        await db.Database.EnsureDeletedAsync();
    }
}
