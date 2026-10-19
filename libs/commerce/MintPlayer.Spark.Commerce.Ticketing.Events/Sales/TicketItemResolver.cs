using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Sales;

/// <summary>Everything one ticketing item is resolved from, loaded by <see cref="TicketCapacitySource"/>.</summary>
/// <param name="Dates">The item's dates: one, or a pass's dates.</param>
/// <param name="EventDateIds">Every date of the event (a phase without its own list sells them all).</param>
/// <param name="Seating">The snapshot of a seated date, when the item's date has one.</param>
/// <param name="ParentType">GB2: the ticket type of the parent line of an add-on, when the request names one.</param>
public sealed record TicketItemContext(
    TicketItemRef Ref,
    TicketEvent Event,
    TicketSalePhase Phase,
    TicketType Type,
    IReadOnlyList<TicketEventDate> Dates,
    IReadOnlyList<string> EventDateIds,
    SnapshotInventory? Seating,
    CommerceChannelOptions Channel,
    TicketItemRef? ParentRef = null,
    TicketType? ParentType = null);

/// <summary>Option keys a ticketing line understands (<see cref="CheckoutItemRequest.Options"/>).</summary>
public static class TicketOptions
{
    /// <summary>The phase's presale access code (D17).</summary>
    public const string Presale = "presale";

    /// <summary>Best available along a chain of the date's layout (D7, D25): the chain key.</summary>
    public const string Chain = "chain";

    /// <summary>Places in a standing or unnumbered block of the date's layout: the block key.</summary>
    public const string Block = "block";
}

/// <summary>
/// Turns a ticketing item and the buyer's request into what checkout sells (PRD §4.1 "capacity sources", §4.11). Pure:
/// the documents are loaded by <see cref="TicketCapacitySource"/>. Answers null when the item is not on sale now; throws
/// a <see cref="CheckoutException"/> when the request itself is wrong (a seat the type may not take, a missing seat
/// choice).
/// </summary>
public static class TicketItemResolver
{
    public static CheckoutItem? Resolve(TicketItemContext context, CheckoutItemRequest request, DateTimeOffset now)
    {
        var (item, ev, phase, type) = (context.Ref, context.Event, context.Phase, context.Type);

        // --- is it on sale? ------------------------------------------------------------------------------------------
        if (!ev.IsPublished || ev.Status == ETicketEventStatus.Cancelled)
            return null;
        if (phase.EventId != ev.Id || type.EventId != ev.Id)
            return null;
        if (!SaleWindows.IsOpen(phase, now))
            return null;
        if (!SaleWindows.PresaleAccepted(phase.PresaleCodeHash, Option(request, TicketOptions.Presale)))
            return null;
        var price = phase.Prices.FirstOrDefault(p => p.TicketTypeId == type.Id && string.Equals(p.Category ?? "", item.Category, StringComparison.Ordinal));
        if (price is null)
            return null;

        var dates = context.Dates;
        if (dates.Count == 0 || dates.Any(d => d.EventId != ev.Id || !SaleWindows.IsSellable(d, now)))
            return null;
        var phaseDates = SaleWindows.PhaseDates(phase, context.EventDateIds).ToHashSet(StringComparer.Ordinal);
        if (item.IsPass)
        {
            // A pass is valid on its own dates; the phase must sell each of them.
            if (type.Kind != ETicketTypeKind.Pass || type.DateIds.Count == 0 || dates.Count != type.DateIds.Distinct().Count()
                || dates.Any(d => !phaseDates.Contains(d.Id!)))
                return null;
        }
        else
        {
            if (type.Kind == ETicketTypeKind.Pass || dates.Count != 1 || !phaseDates.Contains(dates[0].Id!)
                || (type.DateIds.Count > 0 && !type.DateIds.Contains(dates[0].Id!)))
                return null;
        }

        // --- GB2 add-ons ---------------------------------------------------------------------------------------------
        IReadOnlyList<string> parents = [];
        if (type.Kind == ETicketTypeKind.AddOn)
        {
            if (context.ParentRef is not { } parentRef || context.ParentType is not { } parentType || !IsValidParent(context, parentRef, parentType))
                return null;
            parents = [request.ParentItemId!];
        }

        // --- capacity ------------------------------------------------------------------------------------------------
        var quantity = request.Quantity;
        if (quantity < 1)
            throw CheckoutException.Of(CheckoutRefusals.InvalidQuantity);
        var policy = SalePolicies.Effective(ev.Policy, context.Channel);
        var capacity = Capacity(context, request, quantity, policy);

        return new CheckoutItem
        {
            ItemId = request.ItemId,
            Channel = ev.Channel,
            // D2: the event's own hold time, clamped to the channel's bounds (Checkout clamps once more).
            HoldTime = policy.Hold,
            OrganizationId = ev.OrganizationId,
            Price = price.Price,
            PriceBasis = PriceBasis.Gross,
            VatRateBasisPoints = type.VatRateBasisPoints,
            Title = Title(ev, type),
            Description = type.Description,
            ServiceDate = dates.Min(d => d.StartsAt),
            MinimumAge = Max(ev.MinimumAge, type.MinimumAge),
            IsPhysicalGoods = false,
            LimitScope = TicketScopes.Limit(ev.Id!, phase.Id!, policy.LimitScope),
            PerBuyerLimit = policy.PerBuyerLimit,
            // The waiting room's scope; the phase's own presale code was checked above (D17), so a queued presale phase
            // needs both. A capacity source may add a promotions:{scope} for a code from Promotions (combined, AND).
            AdmissionScopes = phase.QueueEnabled ? [TicketScopes.Admission(phase.Id!)] : [],
            RequiresParentItemIds = parents,
            Capacity = capacity,
        };
    }

    /// <summary>GB2: the parent is a ticket of the same event that the add-on may go with, for the same date (or a pass covering it).</summary>
    internal static bool IsValidParent(TicketItemContext context, TicketItemRef parentRef, TicketType parentType)
    {
        if (parentType.EventId != context.Event.Id || parentType.Kind == ETicketTypeKind.AddOn)
            return false;
        if (context.Type.ParentTypeIds.Count > 0 && !context.Type.ParentTypeIds.Contains(parentType.Id!))
            return false;
        var addOnDates = context.Dates.Select(d => d.Id!).ToList();
        var parentDates = parentRef.IsPass ? parentType.DateIds : [parentRef.DateId];
        return addOnDates.All(parentDates.Contains);
    }

    private static CheckoutCapacity Capacity(TicketItemContext context, CheckoutItemRequest request, int quantity, EffectiveSalePolicy policy)
    {
        var (type, phase) = (context.Type, context.Phase);
        var quotas = new List<QuotaLine>();
        foreach (var date in context.Dates)
        {
            if (type.CapacityPerDate is not null)
                quotas.Add(new QuotaLine(TicketQuotas.TypeOnDate(date.Id!, type.Id!), quantity));
            if (phase.CapacityPerDate is not null)
                quotas.Add(new QuotaLine(TicketQuotas.PhaseOnDate(date.Id!, phase.Id!), quantity));
        }

        // An add-on takes no place of the date (parking, lockers): only its own cap.
        if (type.Kind == ETicketTypeKind.AddOn)
            return new CheckoutCapacity { Quotas = quotas };

        if (context.Seating is not { } seating)
        {
            foreach (var date in context.Dates)
                quotas.Add(new QuotaLine(TicketQuotas.Date(date.Id!), quantity));
            return new CheckoutCapacity { Quotas = quotas };
        }

        // A seated date (D7): picked seats, best available along a chain, or places in a block.
        var category = context.Ref.Category;
        if (Option(request, TicketOptions.Block) is { Length: > 0 } blockKey)
        {
            var block = seating.Plan.Blocks.FirstOrDefault(b => b.Key == blockKey && b.Kind != BlockKind.SeatedNumbered);
            if (block is null || !CategoryMatches(block.Category, category))
                throw CheckoutException.Of(CheckoutRefusals.ItemNotFound, subject: "block:" + blockKey);
            quotas.Add(seating.Places(block.Key, quantity));
            return new CheckoutCapacity { Quotas = quotas };
        }
        if (Option(request, TicketOptions.Chain) is { Length: > 0 } chainKey)
        {
            var chain = seating.Plan.Chains.FirstOrDefault(c => c.Key == chainKey);
            if (chain is null || chain.Blocks.Any(cb => seating.Plan.Blocks.FirstOrDefault(b => b.Key == cb.BlockKey) is not { } b || !CategoryMatches(b.Category, category)))
                throw CheckoutException.Of(CheckoutRefusals.ItemNotFound, subject: "chain:" + chainKey);
            return new CheckoutCapacity { Quotas = quotas, BestAvailable = [seating.BestAvailable(chain.Key, quantity)] };
        }

        var seats = SeatKeys(seating, request);
        if (seats.Count == 0)
            throw CheckoutException.Of(CheckoutRefusals.InvalidQuantity, subject: "seat");
        if (seats.Count != quantity)
            throw CheckoutException.Of(CheckoutRefusals.InvalidQuantity, subject: "seats");
        foreach (var seatKey in seats)
        {
            if (!seating.HasSeat(seatKey) || !CategoryMatches(SeatCategory(seating.Plan, seatKey), category))
                throw CheckoutException.Of(CheckoutRefusals.UnitTaken, subject: seatKey);
        }
        // Default 3: wheelchair spaces and companion seats only for the types that allow them.
        if (AccessibleSeating.Check(seating.Plan, seats, type.AllowedSeatTags) is { Count: > 0 } violations)
            throw CheckoutException.Of(CheckoutRefusals.UnitTaken, subject: "restricted:" + violations[0].SeatKey);
        return new CheckoutCapacity { Quotas = quotas, Picks = seating.Picks(seats, policy.SingleSeatRule) };
    }

    /// <summary>The seat keys a request names: its units (seat keys or unit ids), else the seats of its picks.</summary>
    internal static IReadOnlyList<string> SeatKeys(SnapshotInventory seating, CheckoutItemRequest request)
    {
        IEnumerable<string> raw = request.Units is { Count: > 0 } units ? units
            : request.Picks is { Count: > 0 } picks ? picks.SelectMany(p => p.Picked) : [];
        return raw.Select(u => seating.SeatKeyOf(u) ?? u).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? SeatCategory(LayoutPlan plan, string seatKey)
    {
        foreach (var block in plan.Blocks)
            foreach (var row in block.Rows)
                if (row.Seats.FirstOrDefault(s => s.Key == seatKey) is { } seat)
                    return seat.Category ?? block.Category;
        return null;
    }

    /// <summary>An empty price category prices every place; otherwise the place's category must be it.</summary>
    private static bool CategoryMatches(string? placeCategory, string priceCategory)
        => priceCategory.Length == 0 || string.Equals(placeCategory, priceCategory, StringComparison.Ordinal);

    private static string? Option(CheckoutItemRequest request, string key)
        => request.Options is { } options && options.TryGetValue(key, out var value) ? value : null;

    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>"{event} · {ticket type}" in every language either names (the main characteristics, CRD Art. 6(1)(a)).</summary>
    internal static TranslatedString Title(TicketEvent ev, TicketType type)
    {
        var title = new TranslatedString();
        var tags = ev.Name.Tags.Concat(type.Name.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tags.Count == 0)
            return title.Set(TranslatedString.DefaultLanguage, "Ticket");
        foreach (var tag in tags)
        {
            var eventName = ev.Name.IsEmpty ? null : ev.Name.GetValue(tag);
            var typeName = type.Name.IsEmpty ? null : type.Name.GetValue(tag);
            title.Set(tag, string.Join(" · ", new[] { eventName, typeName }.Where(s => !string.IsNullOrWhiteSpace(s))));
        }
        return title;
    }
}
