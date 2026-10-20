namespace MintPlayer.Spark.Sms;

/// <summary>
/// Sends one text message (<c>docs/commerce_PRD.md</c> X13, D29). The framework picks no vendor (K7): an
/// adapter implements this interface and the host registers the one it chose. Phone verification in
/// <c>MintPlayer.Spark.Authorization</c> sends through it.
/// </summary>
/// <remarks>
/// <para>
/// An adapter sends exactly what it is given: <see cref="SparkSmsMessage.To"/> is already E.164
/// (<c>+</c>, country calling code, subscriber number; no spaces), and the body is final.
/// </para>
/// <para>
/// A refusal by the vendor (an unreachable number, a blocked destination) is answered as a receipt with
/// <see cref="SparkSmsReceipt.Accepted"/> false, not thrown; an exception means the vendor could not be
/// reached at all. Neither is retried here.
/// </para>
/// <para>
/// ⚠️ The body may hold a one-time code. An adapter never logs it, and never logs the vendor's credentials.
/// </para>
/// </remarks>
public interface ISparkSmsSender
{
    /// <summary>Hands <paramref name="message"/> to the vendor.</summary>
    Task<SparkSmsReceipt> SendAsync(SparkSmsMessage message, CancellationToken cancellationToken = default);
}

/// <summary>One text message.</summary>
/// <param name="To">The recipient in E.164 form, for example <c>+32470123456</c>.</param>
/// <param name="Body">The text, final and already localized.</param>
/// <param name="Purpose">What the message is for (<c>phone-verification</c>), for the vendor's reporting tags and for tests; never shown.</param>
public sealed record SparkSmsMessage(string To, string Body, string? Purpose = null);

/// <summary>What the vendor answered.</summary>
/// <param name="Accepted">Whether the vendor took the message for delivery. Delivery itself is not confirmed.</param>
/// <param name="ProviderMessageId">The vendor's id of the message, when it gives one.</param>
/// <param name="Error">Why the vendor refused, in the vendor's words; null when accepted.</param>
public sealed record SparkSmsReceipt(bool Accepted, string? ProviderMessageId = null, string? Error = null);
