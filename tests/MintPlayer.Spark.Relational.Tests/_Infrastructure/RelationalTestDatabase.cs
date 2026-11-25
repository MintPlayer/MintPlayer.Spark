using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Relational.Schema;
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

    /// <summary>
    /// A new database with the framework schema (its migrations) and the tables of <paramref name="types"/> (the model's
    /// migration operations, D33), never <c>EnsureCreated</c>.
    /// </summary>
    public static async Task<RelationalTestDatabase> CreateAsync(
        RelationalTestServer server, IEnumerable<Type> types, Action<ModelBuilder>? configureModel = null, string? database = null)
    {
        var store = Open(server, types, configureModel, database ?? "spark_rel_" + Guid.NewGuid().ToString("N")[..12]);
        await RelationalSchema.CreateFromModelAsync(store);
        return new RelationalTestDatabase(store);
    }

    /// <summary>A store over <paramref name="database"/> with the model of <paramref name="types"/>; creates nothing.</summary>
    public static RelationalSparkStore Open(RelationalTestServer server, IEnumerable<Type> types, Action<ModelBuilder>? configureModel, string database)
    {
        var model = SparkRelationalModel.Build(types, t => SparkRelationalExtensions.KindOf(t, new SparkIdOptions()));
        return new RelationalSparkStore(
            new SparkContextSetup(model, server.Dialect, configureModel is null ? [] : [configureModel]), server.ConnectionStringFor(database));
    }

    public async ValueTask DisposeAsync()
    {
        await using var db = Store.NewContext();
        await db.Database.EnsureDeletedAsync();
    }
}
