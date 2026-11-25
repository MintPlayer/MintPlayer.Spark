using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// SQL Server as a test database (#536 D35). Microsoft does not redistribute the engine, so the server is, in order:
/// <list type="number">
///   <item><c>SPARK_TEST_SQLSERVER</c>, a connection string to a server (a local instance, or CI's service container);</item>
///   <item>LocalDB (<c>(localdb)\MSSQLLocalDB</c>) on Windows, when it answers;</item>
///   <item>a Testcontainers SQL Server container (needs Docker), started once per process.</item>
/// </list>
/// Each test database is created on the server and dropped when disposed.
/// </summary>
public sealed class SqlServerSparkTestDatabaseProvider : ISparkTestDatabaseProvider
{
    /// <summary>The connection string to a server, without a database.</summary>
    public const string Variable = "SPARK_TEST_SQLSERVER";

    /// <summary>The container image of the last resort.</summary>
    public const string ContainerImage = "mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04";

    private const string LocalDb = @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? server;

    public string Name => "SqlServer";

    public async Task<ISparkTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var serverConnectionString = await ServerAsync(cancellationToken);
        var name = RelationalSparkTestDatabase.NewName();
        var connectionString = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = name }.ConnectionString;
        return new RelationalSparkTestDatabase(
            Name, connectionString,
            (spark, cs) => spark.UseSqlServer(o => o.ConnectionString = cs),
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

        if (OperatingSystem.IsWindows() && await AnswersAsync(LocalDb, cancellationToken))
            return LocalDb;

        var container = new MsSqlBuilder(ContainerImage).Build();
        await container.StartAsync(cancellationToken);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { container.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* Best effort: Testcontainers' reaper removes it too. */ }
        };
        return new SqlConnectionStringBuilder(container.GetConnectionString()) { TrustServerCertificate = true }.ConnectionString;
    }

    private static async Task<bool> AnswersAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    private static async Task DropAsync(string serverConnectionString, string name, string connectionString, CancellationToken cancellationToken)
    {
        var quoted = "[" + RelationalSparkTestDatabase.Checked(name) + "]";
        using (var pooled = new SqlConnection(connectionString))
            SqlConnection.ClearPool(pooled);

        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(@name) IS NOT NULL
            BEGIN
                ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quoted};
            END
            """;
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
