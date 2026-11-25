namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>One row of <see cref="ISparkSession.StreamAsync{T}"/>: the document, its id and its version.</summary>
/// <param name="Id">The stored id; <see langword="null"/> for a projection that has none.</param>
/// <param name="Version">The version the row was read at (RavenDB: its change vector).</param>
public readonly record struct SparkStreamResult<T>(string? Id, T Document, string? Version);
