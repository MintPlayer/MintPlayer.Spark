namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Refuses a write because the data it was made against is no longer what is stored: the optimistic-concurrency
/// refusal a person gets as a 409 (<c>docs/guide-concurrency.md</c>). The framework's own concurrency exception derives
/// from it, so an add-on may throw it from an interceptor or an <c>ISaveReplacement</c> and get the same answer
/// (warehouse §4.2.5: a write-back the owner refused as a conflict, shown to the person who made the edit).
/// <para>
/// The endpoints answer <c>409 { error, reason, message, attributes }</c> and never <see cref="Exception.Message"/>,
/// which may carry change vectors. <see cref="Reason"/>, <see cref="UserMessage"/> and <see cref="Attributes"/> are
/// what the caller may see.
/// </para>
/// </summary>
public class SparkConflictException : Exception
{
    /// <summary>The row changed since the caller loaded it.</summary>
    public const string Changed = "changed";

    /// <summary>The row was deleted since the caller loaded it (#467, D15).</summary>
    public const string Deleted = "deleted";

    /// <summary>The object already exists (#467, D16).</summary>
    public const string Exists = "exists";

    /// <param name="reason"><see cref="Changed"/>, <see cref="Deleted"/> or <see cref="Exists"/>; safe to send.</param>
    /// <param name="userMessage">A message safe to show, or null to let the client word it.</param>
    /// <param name="attributes">The attributes that conflicted, when the refusal is per attribute.</param>
    public SparkConflictException(string reason, string? userMessage = null, IReadOnlyList<string>? attributes = null)
        : this(reason, userMessage ?? $"Concurrency conflict ({reason}).", userMessage, attributes, null)
    {
    }

    /// <summary>For derived exceptions whose own message carries detail that must never be sent.</summary>
    protected SparkConflictException(string reason, string message, string? userMessage, IReadOnlyList<string>? attributes, Exception? innerException)
        : base(message, innerException)
    {
        Reason = reason;
        UserMessage = userMessage;
        Attributes = attributes ?? [];
    }

    /// <summary><see cref="Changed"/>, <see cref="Deleted"/> or <see cref="Exists"/>; safe to send.</summary>
    public string Reason { get; }

    /// <summary>
    /// A message safe to show: a bulk delete names the rows that changed (#467, D18), a write-back names the owner.
    /// Null for a single row, where the client words it.
    /// </summary>
    public string? UserMessage { get; }

    /// <summary>The attributes whose stored value is no longer the one the edit started from; empty when not per attribute.</summary>
    public IReadOnlyList<string> Attributes { get; }
}
