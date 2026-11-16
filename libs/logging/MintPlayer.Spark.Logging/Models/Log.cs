using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Logging.Models;

/// <summary>
/// What was logged inside one log scope (<see cref="ILoggerScope"/>), in order. Written only by the scope; read as a
/// persistent object whose access the shipped <c>LogActions</c> inherits from <see cref="RelatedPersistentObjectId"/>.
/// </summary>
public class Log : IExpiring
{
    /// <summary><c>Logs/{key}</c>: a generated Guid, or the key a feature passed to reopen the same log.</summary>
    public string? Id { get; set; }

    /// <summary>The record this log belongs to, if any: the log is readable exactly when that record is.</summary>
    public string? RelatedPersistentObjectId { get; set; }

    public List<LogEntry> Entries { get; set; } = [];

    /// <inheritdoc />
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>One logged message.</summary>
public class LogEntry
{
    /// <summary>When it was logged (UTC).</summary>
    public DateTime At { get; set; }

    /// <summary>The <see cref="Microsoft.Extensions.Logging.LogLevel"/> name.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>The formatted message.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The exception passed to <c>LogX(ex, …)</c>, in full. Its own field so an application can hide it from readers who
    /// may see the log but not the internals (<c>GetProtectedAttributesAsync</c> with <c>Entries.Exception</c>).
    /// </summary>
    public string? Exception { get; set; }
}
