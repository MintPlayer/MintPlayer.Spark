using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace MintPlayer.Spark.Relational.Tests.Behaviour;

/// <summary>
/// An empty database for the D31 behaviour tests: they test the database server itself, with plain SQL, not Spark.
/// The name is generated and spliced into <c>CREATE</c>/<c>DROP</c> only after a pattern check.
/// </summary>
public abstract class BehaviourDatabase : IAsyncLifetime
{
    private string? serverConnectionString;

    protected string Name { get; } = "spark_beh_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>The server's connection string, or <see langword="null"/> when its variable is unset (the tests skip).</summary>
    protected abstract string? ServerConnectionString { get; }

    protected abstract DbConnection NewConnection(string? database);

    protected abstract string CreateSql { get; }

    protected abstract string DropSql { get; }

    protected abstract void ClearPools();

    protected string CheckedName => System.Text.RegularExpressions.Regex.IsMatch(Name, "^spark_beh_[0-9a-f]{12}$")
        ? Name
        : throw new InvalidOperationException("Unexpected database name.");

    public async Task InitializeAsync()
    {
        serverConnectionString = ServerConnectionString;
        if (serverConnectionString is null)
            return;
        await using var server = NewConnection(null);
        await server.OpenAsync();
        await ExecuteAsync(server, CreateSql);
    }

    public async Task DisposeAsync()
    {
        if (serverConnectionString is null)
            return;
        ClearPools();
        await using var server = NewConnection(null);
        await server.OpenAsync();
        await ExecuteAsync(server, DropSql);
    }

    /// <summary>A new open connection to the test database.</summary>
    protected async Task<DbConnection> OpenAsync()
    {
        if (serverConnectionString is null)
            throw new SkipException("No server is configured for this dialect.");
        var connection = NewConnection(Name);
        await connection.OpenAsync();
        return connection;
    }

    protected static async Task<int> ExecuteAsync(DbConnection connection, string sql, DbTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    protected static async Task<T> ScalarAsync<T>(DbConnection connection, string sql, DbTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, sql, transaction, parameters);
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    protected static async Task<List<T>> ColumnAsync<T>(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        await using var command = Command(connection, sql, transaction, []);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<T>();
        while (await reader.ReadAsync())
            values.Add(reader.GetFieldValue<T>(0));
        return values;
    }

    private static DbCommand Command(DbConnection connection, string sql, DbTransaction? transaction, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}

public abstract class SqlServerBehaviourDatabase : BehaviourDatabase
{
    protected override string? ServerConnectionString => Environment.GetEnvironmentVariable("SPARK_TEST_SQLSERVER") is { Length: > 0 } cs ? cs : null;

    protected override DbConnection NewConnection(string? database)
        => new SqlConnection(new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = database ?? "master" }.ConnectionString);

    protected override string CreateSql => $"CREATE DATABASE [{CheckedName}]";

    protected override string DropSql => $"IF DB_ID('{CheckedName}') IS NOT NULL BEGIN ALTER DATABASE [{CheckedName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{CheckedName}]; END";

    protected override void ClearPools() => SqlConnection.ClearAllPools();
}

public abstract class PostgreSqlBehaviourDatabase : BehaviourDatabase
{
    protected override string? ServerConnectionString => Environment.GetEnvironmentVariable("SPARK_TEST_POSTGRESQL") is { Length: > 0 } cs ? cs : null;

    protected override DbConnection NewConnection(string? database)
        => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = database ?? "postgres" }.ConnectionString);

    protected override string CreateSql => $"CREATE DATABASE \"{CheckedName}\"";

    protected override string DropSql => $"DROP DATABASE IF EXISTS \"{CheckedName}\" WITH (FORCE)";

    protected override void ClearPools() => NpgsqlConnection.ClearAllPools();
}
