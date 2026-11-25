using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Relational;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Testing;
using Npgsql;

namespace MintPlayer.Spark.E2E.Tests._Infrastructure;

/// <summary>
/// The database of an app on PostgreSQL (#536), on the test server (<see cref="PostgreSqlSparkTestDatabaseProvider"/>:
/// <c>SPARK_TEST_POSTGRESQL</c>'s, or the embedded instance). The host creates it empty; the app applies its schema at
/// startup, as it does in production. Dropped on dispose.
/// </summary>
public sealed partial class PostgreSqlAppDatabase : IAsyncDisposable
{
    private readonly string serverConnectionString;

    private PostgreSqlAppDatabase(string serverConnectionString, string name)
    {
        this.serverConnectionString = serverConnectionString;
        Name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = name }.ConnectionString;
    }

    public string Name { get; }

    /// <summary>The app's <c>ConnectionStrings:Spark</c>.</summary>
    public string ConnectionString { get; }

    public static async Task<PostgreSqlAppDatabase> CreateAsync(string name)
    {
        // An identifier cannot be a parameter: the name is checked against a strict pattern before it is spliced.
        if (!SafeName().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a database name this host creates.", nameof(name));

        var database = new PostgreSqlAppDatabase(await PostgreSqlSparkTestDatabaseProvider.ServerConnectionStringAsync(), name);
        await database.ExecuteOnServerAsync($"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE)""");
        await database.ExecuteOnServerAsync($"""CREATE DATABASE "{name}" """);
        return database;
    }

    /// <summary>
    /// Confirms <paramref name="email"/>'s account and puts it in <paramref name="group"/>, by claim and by role, as the
    /// RavenDB hosts patch their admin. Through the framework's own user store, never raw SQL: a user row carries its
    /// claims as JSON, a version the app checks on its next save, and a change stamp. Returns the user's id.
    /// </summary>
    public async Task<string> PromoteAsync(string email, string? group, string? role)
    {
        var model = SparkRelationalModel.Build([typeof(SparkUser)], t => SparkRelationalExtensions.KindOf(t, new SparkIdOptions()));
        var store = new RelationalSparkStore(new SparkContextSetup(model, new PostgreSqlSparkDialect(), []), ConnectionString);

        using var users = new UserStore<SparkUser>(store);
        var user = await users.FindByEmailAsync(email.ToUpperInvariant(), default)
            ?? throw new InvalidOperationException($"Registered user '{email}' not found in PostgreSQL database '{Name}'.");

        user.EmailConfirmed = true;
        if (group is not null && !user.Claims.Any(c => c.ClaimType == "group" && c.ClaimValue == group))
            user.Claims.Add(new SparkUserClaim { ClaimType = "group", ClaimValue = group });
        if (role is not null && !user.Roles.Contains(role))
            user.Roles.Add(role);

        var result = await users.UpdateAsync(user, default);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Promoting '{email}' failed: {string.Join(", ", result.Errors.Select(e => e.Code))}.");
        return user.Id!;
    }

    public async ValueTask DisposeAsync()
    {
        using (var pooled = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(pooled);
        try
        {
            await ExecuteOnServerAsync($"""DROP DATABASE IF EXISTS "{Name}" WITH (FORCE)""");
        }
        catch
        {
            // Best-effort, as the RavenDB databases' cleanup is.
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = "postgres" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,63}$")]
    private static partial Regex SafeName();
}
