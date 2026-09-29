namespace MintPlayer.Spark.Messaging.Models;

/// <summary>
/// Sidecar for <c>IMessageProgress</c>: the steps one handler of one message has completed. Id
/// <c>{messageId}/progress/{handlerIndex}</c>, its own collection (<c>SparkMessageProgresses</c>), and
/// the same <c>@expires</c> as its message once that message is terminal.
/// <para>
/// A sidecar rather than an array on <see cref="SparkMessage"/>: a fan-out handler can mark thousands
/// of steps, and every one of them would otherwise rewrite — and re-deliver to the subscription — the
/// whole message document.
/// </para>
/// </summary>
public class SparkMessageProgress
{
    public string? Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public int HandlerIndex { get; set; }
    public List<string> Steps { get; set; } = [];

    public static string IdFor(string messageId, int handlerIndex) => $"{messageId}/progress/{handlerIndex}";
}
