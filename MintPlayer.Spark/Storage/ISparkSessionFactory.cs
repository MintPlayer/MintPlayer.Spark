namespace MintPlayer.Spark.Storage;

/// <summary>
/// Factory for creating storage sessions.
/// Replaces direct dependency on database-specific document store types.
/// </summary>
public interface ISparkSessionFactory
{
    /// <summary>
    /// Opens a new session for database operations.
    /// </summary>
    ISparkSession OpenSession();
}
