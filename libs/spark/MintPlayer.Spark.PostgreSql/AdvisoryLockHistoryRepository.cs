using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace MintPlayer.Spark;

/// <summary>
/// npgsql/efcore.pg#4162 (#536 S19, D33): Npgsql 11's history repository locks the history table inside each
/// migration's transaction, so the lock ends at each commit while the migrator planned the run once, and a second
/// instance applies the next migration in between. This one holds a session advisory lock for the whole run, as
/// SQL Server's <c>sp_getapplock</c> does. It goes when Npgsql ships npgsql/efcore.pg#4163.
/// </summary>
#pragma warning disable EF1001 // NpgsqlHistoryRepository is internal API; this class exists only until the upstream fix ships.
internal sealed class AdvisoryLockHistoryRepository : Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal.NpgsqlHistoryRepository
{
    public AdvisoryLockHistoryRepository(HistoryRepositoryDependencies dependencies) : base(dependencies) { }
#pragma warning restore EF1001

    public override LockReleaseBehavior LockReleaseBehavior => LockReleaseBehavior.Connection;

    public override IMigrationsDatabaseLock AcquireDatabaseLock()
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        Command($"SELECT pg_advisory_lock({LockKey})").ExecuteNonQuery(Parameters());
        return new Lock(this);
    }

    public override async Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
    {
        Dependencies.MigrationsLogger.AcquiringMigrationLock();
        await Command($"SELECT pg_advisory_lock({LockKey})").ExecuteNonQueryAsync(Parameters(), cancellationToken);
        return new Lock(this);
    }

    /// <summary>
    /// 64-bit FNV-1a of the history table's name, spliced as a number: no text from configuration reaches the SQL.
    /// </summary>
    private long LockKey
    {
        get
        {
            var hash = 14695981039346656037UL;
            foreach (var b in Encoding.UTF8.GetBytes($"efcore:migrations:{TableSchema}.{TableName}"))
                hash = (hash ^ b) * 1099511628211UL;
            return unchecked((long)hash);
        }
    }

    private IRelationalCommand Command(string sql) => Dependencies.RawSqlCommandBuilder.Build(sql);

    private RelationalCommandParameterObject Parameters()
        => new(Dependencies.Connection, null, null, Dependencies.CurrentContext.Context, Dependencies.CommandLogger, CommandSource.Migrations);

    private sealed class Lock : IMigrationsDatabaseLock
    {
        private readonly AdvisoryLockHistoryRepository repository;

        public Lock(AdvisoryLockHistoryRepository repository) => this.repository = repository;

        public IHistoryRepository HistoryRepository => repository;

        public void Dispose()
        {
            if (repository.Dependencies.Connection.DbConnection.State == ConnectionState.Open)
                repository.Command($"SELECT pg_advisory_unlock({repository.LockKey})").ExecuteNonQuery(repository.Parameters());
        }

        public async ValueTask DisposeAsync()
        {
            if (repository.Dependencies.Connection.DbConnection.State == ConnectionState.Open)
                await repository.Command($"SELECT pg_advisory_unlock({repository.LockKey})").ExecuteNonQueryAsync(repository.Parameters());
        }
    }
}
