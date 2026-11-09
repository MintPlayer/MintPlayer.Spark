namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// Reads the <b>stored</b> form of embedded rows, for the startup check that refuses keyless rows (#551 D18; see core's
/// <c>ValueObjectKeyVerifier</c>). It has to ask the store: a row stored without its key deserializes with a freshly
/// minted one, so in memory every row looks keyed.
/// </summary>
/// <remarks>A provider that cannot hold a keyless row (an in-memory store fed only through Spark) registers none.</remarks>
public interface ISparkStoreRowKeyScan
{
    /// <summary>
    /// How many documents of <paramref name="collection"/> hold at least one row at <paramref name="path"/> whose
    /// <paramref name="keyProperty"/> is absent, null or "". The path is dot-separated, and every array met on the way
    /// is flattened, so <c>Sessions.Reports</c> reaches the reports of every session. A null element is not a row.
    /// </summary>
    Task<int> CountDocumentsWithKeylessRowsAsync(string collection, string path, string keyProperty, CancellationToken cancellationToken);
}
