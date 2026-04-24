namespace MintPlayer.Spark.Abstractions.ClientInstructions;

/// <summary>
/// Shows a toast/notification on the frontend. <see cref="DurationMs"/> is optional —
/// the frontend applies its default when null.
/// </summary>
public sealed class NotifyInstruction : ClientInstruction
{
    public required string Message { get; init; }
    public NotificationKind Kind { get; init; } = NotificationKind.Info;
    public int? DurationMs { get; init; }
}
