namespace MintPlayer.Spark.Commerce.Ticketing.Events;

/// <summary>
/// Why ticketing refused something. Codes are permanent: the client translates <c>ticketingEvents.refusal.{code}</c>
/// (translations.json) and the endpoints answer them in <c>SparkTicketingRefusal</c> with HTTP 409.
/// </summary>
public static class TicketingRefusals
{
    public const string EventNotFound = "eventNotFound";
    public const string UnknownChannel = "unknownChannel";
    public const string InvalidEvent = "invalidEvent";
    public const string NotPublishable = "notPublishable";
    public const string Forbidden = "forbidden";
    public const string AnswersRefused = "answersRefused";
    public const string AnswersClosed = "answersClosed";

    /// <summary>
    /// GB3, raised through Checkout's pre-payment check: a required "at checkout" question is unanswered or invalid. The
    /// refusal's line id is the order line, its subject <c>{position}:{questionKey}:{code}</c>.
    /// </summary>
    public const string AttendeeAnswersRequired = "attendeeAnswersRequired";
}

/// <summary>A ticketing refusal: a <see cref="TicketingRefusals"/> code, an English message for logs, and what it concerns.</summary>
public sealed class TicketingException(string code, string message, string? subject = null) : Exception(message)
{
    public string Code { get; } = code;

    public string? Subject { get; } = subject;

    public static TicketingException Of(string code, string? subject = null, string? message = null)
        => new(code, message ?? $"Ticketing refused: {code}{(subject is null ? "" : " (" + subject + ")")}.", subject);
}
