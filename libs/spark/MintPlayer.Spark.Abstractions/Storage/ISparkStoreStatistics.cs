namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>One collection (a table on the relational providers) in <see cref="SparkStoreStatistics"/>.</summary>
/// <param name="Name">The collection's name, as the store spells it.</param>
/// <param name="Documents">How many documents (rows) it holds.</param>
/// <param name="SizeBytes">Its size on disk, or <see langword="null"/> when the store cannot tell.</param>
public sealed record SparkCollectionStatistics(string Name, long Documents, long? SizeBytes);

/// <summary>The database's size and its collections, largest first (<see cref="ISparkStoreStatistics"/>).</summary>
/// <param name="SizeBytes">The database's size on disk, or <see langword="null"/> when the store cannot tell (memory).</param>
/// <param name="Documents">Every collection's documents together.</param>
public sealed record SparkStoreStatistics(long? SizeBytes, long Documents, IReadOnlyList<SparkCollectionStatistics> Collections);

/// <summary>
/// How big the application's database is, per collection (#536 M8, the dashboards' <c>DatabaseStatistics</c>). Every store
/// provider implements it: RavenDB from its statistics operations, SQL Server and PostgreSQL from their catalogs with
/// an exact row count per table, memory from its documents (no size).
/// </summary>
public interface ISparkStoreStatistics
{
    Task<SparkStoreStatistics> GetAsync(CancellationToken cancellationToken = default);
}
