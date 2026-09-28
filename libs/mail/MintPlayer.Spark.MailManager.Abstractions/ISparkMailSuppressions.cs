namespace MintPlayer.Spark.MailManager;

/// <summary>
/// The suppression list: addresses mail must not go to, checked when a mail is queued and again when
/// it is sent. Fed by hard bounces (every stream), one-click unsubscribes (one stream) and the app.
/// Stored as <c>SparkMailSuppressions/{sha256(normalized email)}</c> — the address itself is not kept.
/// </summary>
public interface ISparkMailSuppressions
{
    /// <summary>Whether mail of <paramref name="stream"/> (or any mail, when null) must not go to <paramref name="email"/>.</summary>
    Task<bool> IsSuppressedAsync(string email, string? stream = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suppresses <paramref name="email"/> for <paramref name="stream"/>, or for every stream when
    /// <paramref name="stream"/> is null.
    /// </summary>
    Task SuppressAsync(string email, SparkMailSuppressionReason reason, string? stream = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lifts a suppression: for <paramref name="stream"/>, or entirely when null. Returns whether
    /// anything was lifted.
    /// </summary>
    Task<bool> RemoveAsync(string email, string? stream = null, CancellationToken cancellationToken = default);
}

/// <summary>Why an address is suppressed.</summary>
public enum SparkMailSuppressionReason
{
    /// <summary>A permanent (5.x.x) bounce.</summary>
    Bounce,
    /// <summary>A one-click unsubscribe.</summary>
    Unsubscribe,
    /// <summary>A spam complaint.</summary>
    Complaint,
    /// <summary>Added by the application.</summary>
    Manual,
}
