namespace MintPlayer.Spark.History;

/// <summary>Options for <c>AddHistory()</c>, bound from <c>Spark:History</c>.</summary>
public sealed class SparkHistoryOptions
{
    /// <summary>
    /// Whether startup merges each model type's <c>revisions</c> block into the database's revisions
    /// configuration (T10). Default <see langword="true"/>. Turn it off when an operator manages the
    /// configuration by hand, or when the app's RavenDB credentials may not change it
    /// (<c>ConfigureRevisionsOperation</c> is a database-admin operation) — startup otherwise refuses
    /// with the reason.
    /// </summary>
    public bool ConfigureRevisions { get; set; } = true;
}
