namespace MintPlayer.Spark.Commerce.Ticketing.Tickets;

/// <summary>
/// Why a ticket operation was refused. Codes are permanent: the client translates <c>ticketingTickets.refusal.{code}</c>
/// (translations.json) and the endpoints answer them in <c>SparkTicketRefusal</c> with HTTP 409.
/// </summary>
public static class TicketRefusals
{
    public const string NotFound = "notFound";
    public const string Forbidden = "forbidden";
    public const string NotDelivered = "notDelivered";
    public const string Revoked = "revoked";
    public const string OnHold = "onHold";
    public const string Frozen = "frozen";
    public const string TransferNotAllowed = "transferNotAllowed";
    public const string ResaleNotAllowed = "resaleNotAllowed";
    public const string AboveFaceValue = "aboveFaceValue";
    public const string AlreadyListed = "alreadyListed";
    public const string ListingClosed = "listingClosed";
    public const string TransferPending = "transferPending";
    public const string TransferClosed = "transferClosed";
    public const string NameRequired = "nameRequired";
    public const string EmailRequired = "emailRequired";
    public const string FeeUnpaid = "feeUnpaid";
    public const string InvalidSettings = "invalidSettings";
    public const string DeviceRevoked = "deviceRevoked";
    public const string ListNotFound = "listNotFound";
    public const string TooManyScans = "tooManyScans";
}

/// <summary>A ticket refusal: a <see cref="TicketRefusals"/> code, an English message for logs, and what it concerns.</summary>
public sealed class TicketException(string code, string message, string? subject = null) : Exception(message)
{
    public string Code { get; } = code;

    public string? Subject { get; } = subject;

    public static TicketException Of(string code, string? subject = null, string? message = null)
        => new(code, message ?? $"Ticket refused: {code}{(subject is null ? "" : " (" + subject + ")")}.", subject);
}
