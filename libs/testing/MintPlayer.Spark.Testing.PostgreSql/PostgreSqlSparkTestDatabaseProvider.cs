using MysticMind.PostgresEmbed;
using Npgsql;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// PostgreSQL as a test database (#536 D35). The server is <c>SPARK_TEST_POSTGRESQL</c>'s, a connection string to a
/// server, or else an embedded one: <c>MysticMind.PostgresEmbed</c> downloads zonky's PostgreSQL binaries once, runs
/// <c>initdb</c> and starts <c>postgres</c>, so a developer needs no Docker, as with RavenDB. The embedded server is the
/// named instance <see cref="EmbeddedInstance"/>, which keeps running and which later runs attach to.
/// Each test database is created on the server and dropped when disposed.
/// </summary>
public sealed class PostgreSqlSparkTestDatabaseProvider : ISparkTestDatabaseProvider
{
    /// <summary>The connection string to a server, without a database.</summary>
    public const string Variable = "SPARK_TEST_POSTGRESQL";

    /// <summary>The major version of the embedded server.</summary>
    public const string EmbeddedVersion = "18";

    /// <summary>
    /// The embedded server's instance, shared by every test run on the machine. Stop it with the <c>pgembed</c> CLI,
    /// or delete <c>%LOCALAPPDATA%\pgembed\instances\spark-tests</c> once no server runs from it.
    /// </summary>
    public const string EmbeddedInstance = "spark-tests";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? server;

    public string Name => "PostgreSql";

    public async Task<ISparkTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var serverConnectionString = await ServerAsync(cancellationToken);
        var name = RelationalSparkTestDatabase.NewName();
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = name }.ConnectionString;
        return new RelationalSparkTestDatabase(
            Name, connectionString,
            (spark, cs) => spark.UsePostgreSql(o => o.ConnectionString = cs),
            ct => DropAsync(serverConnectionString, name, connectionString, ct));
    }

    private static async Task<string> ServerAsync(CancellationToken cancellationToken)
    {
        if (server is { } known)
            return known;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return server ??= await LocateAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> LocateAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } configured)
            return configured;

        // One named instance per machine, kept running and attached to by every later run, as a local development
        // database is. Measured 2026-11-25: an ephemeral server per test process (the default) left its nine
        // postgres.exe processes running after the test host exited, and wrote its instance into the test's bin folder.
        // The named instance lives in the shared cache (%LOCALAPPDATA%\pgembed, or PGEMBED_CACHE_DIR), so a run adds
        // nothing to the machine but the databases it drops again.
        var embedded = new PgServer(new PgServerOptions
        {
            PgVersion = EmbeddedVersion,
            InstanceName = EmbeddedInstance,
            AttachIfRunning = true,
            KeepRunningAfterExit = true,
        });
        await embedded.StartAsync(cancellationToken);
        return embedded.ConnectionString;
    }

    private static async Task DropAsync(string serverConnectionString, string name, string connectionString, CancellationToken cancellationToken)
    {
        var quoted = "\"" + RelationalSparkTestDatabase.Checked(name) + "\"";
        using (var pooled = new NpgsqlConnection(connectionString))
            NpgsqlConnection.ClearPool(pooled);

        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = "postgres" }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS {quoted} WITH (FORCE)";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
