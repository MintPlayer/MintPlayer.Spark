namespace MintPlayer.Spark.Abstractions.ClientOperations;

/// <summary>
/// Shows a toast/notification on the frontend. <see cref="DurationMs"/> is optional —
/// the frontend applies its default when null.
/// </summary>
public sealed class NotifyOperation : ClientOperation
{
    /// <summary>The text in the request's language (<c>Accept-Language</c>), for clients that show it as is.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The text in every language, when the server has it (<see cref="IClientAccessor.Notify(TranslatedString, NotificationKind, TimeSpan?)"/>).
    /// ng-spark shows the translation of the language its user picked, which the server never learns
    /// (it is not the browser's <c>Accept-Language</c>) — the same way it resolves labels and
    /// validation messages; <see cref="Message"/> is the fallback.
    /// </summary>
    public TranslatedString? TranslatedMessage { get; init; }

    public NotificationKind Kind { get; init; } = NotificationKind.Info;
    public int? DurationMs { get; init; }
}
