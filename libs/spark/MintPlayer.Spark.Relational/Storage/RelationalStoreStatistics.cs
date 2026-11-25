using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;

namespace MintPlayer.Spark.Relational.Storage;

/// <summary>
/// <see cref="ISparkStoreStatistics"/> on the relational providers (#536 M8): an exact row count per collection table
/// (EF's <c>COUNT</c>), and the sizes from the dialect's catalog queries. A login that may not read the catalog
/// (SQL Server's VIEW DATABASE STATE) still gets the counts, with no sizes.
/// </summary>
public sealed class RelationalStoreStatistics(RelationalSparkStore store) : ISparkStoreStatistics
{
    private static readonly MethodInfo CountOfT = typeof(RelationalStoreStatistics).GetMethod(nameof(CountAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public async Task<SparkStoreStatistics> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = store.NewContext();
        var (size, sizes) = await SizesAsync(db, store.Setup.Dialect, cancellationToken);

        var collections = new List<SparkCollectionStatistics>();
        foreach (var table in db.SparkModel.Tables)
        {
            var count = await (Task<long>)CountOfT.MakeGenericMethod(table.Root).Invoke(null, [db, cancellationToken])!;
            collections.Add(new SparkCollectionStatistics(table.Name, count, sizes.TryGetValue(table.Name, out var bytes) ? bytes : null));
        }

        return new SparkStoreStatistics(size, collections.Sum(c => c.Documents),
            [.. collections.OrderByDescending(c => c.SizeBytes ?? 0).ThenByDescending(c => c.Documents).ThenBy(c => c.Name, StringComparer.Ordinal)]);
    }

    private static Task<long> CountAsync<T>(SparkDbContext db, CancellationToken cancellationToken) where T : class
        => db.Set<T>().LongCountAsync(cancellationToken);

    private static async Task<(long? Size, Dictionary<string, long> Tables)> SizesAsync(SparkDbContext db, SparkRelationalDialect dialect, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long? size = null;
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = dialect.DatabaseSizeSql;
                if (await command.ExecuteScalarAsync(cancellationToken) is { } value and not DBNull)
                    size = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = dialect.TableSizesSql;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    // The same name in two schemas (the framework's and the app's): the larger one is the collection's
                    // table more often than not, and either is the right order of magnitude.
                    var name = reader.GetString(0);
                    var bytes = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    tables[name] = Math.Max(tables.GetValueOrDefault(name), bytes);
                }
            }
        }
        catch (DbException)
        {
            // No rights on the catalog: the counts below still answer.
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
        return (size, tables);
    }
}
