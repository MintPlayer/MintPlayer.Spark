namespace MintPlayer.Spark.Commerce.Ticketing.Events.Sales;

/// <summary>
/// The sellable item ids of ticketing (checkout's <c>ItemId</c>): <c>ticketing:{phaseId}|{ticketTypeId}|{category}|{dateId}</c>,
/// or <c>…|pass</c> for a multi-day pass, which names its dates itself. Ids travel in bodies, never in paths
/// (RavenDB ids contain slashes). The price history (GA3) is kept per price entry, without the date
/// (<see cref="PriceItemId"/>): a price is the same on every date of a phase.
/// </summary>
public static class TicketItemIds
{
    public const string Prefix = "ticketing:";

    /// <summary>The date segment of a pass.</summary>
    public const string PassDate = "pass";

    private const char Separator = '|';

    public static string For(string phaseId, string ticketTypeId, string? category, string dateId)
        => string.Concat(Prefix, phaseId, "|", ticketTypeId, "|", category ?? "", "|", dateId);

    public static string ForPass(string phaseId, string ticketTypeId, string? category) => For(phaseId, ticketTypeId, category, PassDate);

    /// <summary>The price history's item: one per phase × ticket type × category (GA3).</summary>
    public static string PriceItemId(string phaseId, string ticketTypeId, string? category)
        => string.Concat(Prefix, phaseId, "|", ticketTypeId, "|", category ?? "");

    public static bool Handles(string? itemId) => itemId is not null && itemId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The parts of <paramref name="itemId"/>, or null when it is not a well-formed ticketing item.</summary>
    public static TicketItemRef? Parse(string? itemId)
    {
        if (!Handles(itemId))
            return null;
        var parts = itemId![Prefix.Length..].Split(Separator);
        if (parts.Length != 4 || parts[0].Length == 0 || parts[1].Length == 0 || parts[3].Length == 0)
            return null;
        return new TicketItemRef(parts[0], parts[1], parts[2], parts[3]);
    }
}

/// <summary>A parsed ticketing item id.</summary>
/// <param name="Category">The price category; empty for the date's only one.</param>
/// <param name="DateId">The date, or <see cref="TicketItemIds.PassDate"/>.</param>
public sealed record TicketItemRef(string PhaseId, string TicketTypeId, string Category, string DateId)
{
    public bool IsPass => DateId == TicketItemIds.PassDate;

    public string PriceItemId => TicketItemIds.PriceItemId(PhaseId, TicketTypeId, Category);
}

/// <summary>
/// The inventory quotas of ticketing (§4.3, D15). Every one is created when the event is published (idempotent):
/// a date's general-admission places, a ticket type's cap per date, a phase's cap per date. A seated date sells the
/// seats, chains and blocks of its layout snapshot instead of <see cref="Date"/> (Ticketing.Venues' ids).
/// </summary>
public static class TicketQuotas
{
    public static string Date(string dateId) => $"ticketing-quotas/{dateId}";

    public static string TypeOnDate(string dateId, string ticketTypeId) => $"ticketing-quotas/{dateId}/types/{ticketTypeId}";

    public static string PhaseOnDate(string dateId, string phaseId) => $"ticketing-quotas/{dateId}/phases/{phaseId}";

    /// <summary>The shard count of a quota: one per 250 places, at least one, at most <paramref name="max"/>.</summary>
    public static int Shards(int capacity, int max) => Math.Clamp(capacity / 250, 1, Math.Max(1, max));
}

/// <summary>The scopes ticketing hands to checkout.</summary>
public static class TicketScopes
{
    /// <summary>The waiting-room gate of a phase (§4.12): one room per phase, whatever the number of dates.</summary>
    public static string Admission(string phaseId) => $"ticketing-phase:{phaseId}";

    /// <summary>The per-buyer limit scope (D16): the whole event, or one phase.</summary>
    public static string Limit(string eventId, string phaseId, Models.ETicketLimitScope scope)
        => scope == Models.ETicketLimitScope.Phase ? $"ticketing-phase:{phaseId}" : $"ticketing-event:{eventId}";
}
