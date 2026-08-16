namespace MintPlayer.Spark.Exceptions;

/// <summary>
/// Thrown during startup when <c>App_Data/model-hashes.json</c> does not match the model this
/// application would generate from its entity classes and the files on disk.
/// <para>
/// A dedicated type so a host can catch precisely this, tests can assert it, and it does not
/// disappear into a generic <see cref="InvalidOperationException"/> handler.
/// </para>
/// </summary>
public sealed class SparkModelOutOfSyncException(string message) : Exception(message);
