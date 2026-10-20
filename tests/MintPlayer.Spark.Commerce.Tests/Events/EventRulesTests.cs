using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Answers;
using MintPlayer.Spark.Commerce.Ticketing.Events.Discovery;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.PreSelect;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;
using MintPlayer.Spark.Commerce.Tests.Venues;
using MintPlayer.Spark.WaitingRoom;
using static MintPlayer.Spark.Commerce.Tests.Events.EventFixtures;

namespace MintPlayer.Spark.Commerce.Tests.Events;

/// <summary>
/// The pure rules of ticketing events (M13): item ids, per-sale policies clamped to the channel (D2), sale windows and
/// presale codes (D17), the item resolver (passes, add-ons GB2, seated dates D7 with accessible seating default 3),
/// publication checks, attendee questions (GB3), PreSelectOrder's planner (D25) and the .ics writer (GB9).
/// </summary>
public class EventRulesTests
{
    // ---- item ids ----------------------------------------------------------------------------------------------

    [Fact]
    public void Item_ids_round_trip_and_reject_other_items()
    {
        var id = TicketItemIds.For("TicketSalePhases/1-A", "TicketTypes/2-A", "cat1", "TicketEventDates/3-A");
        id.Should().Be("ticketing:TicketSalePhases/1-A|TicketTypes/2-A|cat1|TicketEventDates/3-A");
        var parsed = TicketItemIds.Parse(id)!;
        parsed.PhaseId.Should().Be("TicketSalePhases/1-A");
        parsed.TicketTypeId.Should().Be("TicketTypes/2-A");
        parsed.Category.Should().Be("cat1");
        parsed.DateId.Should().Be("TicketEventDates/3-A");
        parsed.IsPass.Should().BeFalse();
        parsed.PriceItemId.Should().Be("ticketing:TicketSalePhases/1-A|TicketTypes/2-A|cat1");

        TicketItemIds.Parse(TicketItemIds.ForPass("p", "t", null))!.IsPass.Should().BeTrue();
        TicketItemIds.Parse("Products/1-A").Should().BeNull();
        TicketItemIds.Parse("ticketing:p|t|c").Should().BeNull();
        TicketItemIds.Parse("ticketing:|t||d").Should().BeNull();
    }

    // ---- per-sale policies (D2) --------------------------------------------------------------------------------

    [Fact]
    public void Seller_values_are_clamped_to_the_channel_bounds_when_read()
    {
        var channel = Channel(c =>
        {
            c.Hold = new HoldBounds { Default = TimeSpan.FromMinutes(10), Min = TimeSpan.FromMinutes(5), Max = TimeSpan.FromMinutes(20) };
            c.PerBuyerLimit = 4;
            c.Refunds.Allowed = [RefundPolicyKind.None, RefundPolicyKind.UntilDaysBefore];
            c.Refunds.MaxFeeBasisPoints = 500;
            c.Resale.Transfer = true;
        });
        var policy = SalePolicies.Effective(new TicketSalePolicy
        {
            HoldMinutes = 60,
            PerBuyerLimit = 10,
            RefundPolicy = RefundPolicyKind.WithFee,
            RefundFeeBasisPoints = 2000,
            Transfer = true,
            FaceValueResale = true,
            QueueMode = WaitingRoomMode.Ballot,
        }, channel);

        policy.Hold.Should().Be(TimeSpan.FromMinutes(20));
        policy.PerBuyerLimit.Should().Be(4);              // D16: a seller may lower the limit, never raise it
        policy.RefundPolicy.Should().Be(RefundPolicyKind.None); // WithFee is not allowed in the channel
        policy.RefundFeeBasisPoints.Should().Be(500);
        policy.Transfer.Should().BeTrue();
        policy.FaceValueResale.Should().BeFalse();        // the channel's switch is off
        policy.QueueMode.Should().Be(WaitingRoomMode.RandomizedPreQueue); // Ballot is not one of the channel's modes

        var lower = SalePolicies.Effective(new TicketSalePolicy { HoldMinutes = 1, PerBuyerLimit = 2 }, channel);
        lower.Hold.Should().Be(TimeSpan.FromMinutes(5));
        lower.PerBuyerLimit.Should().Be(2);
    }

    // ---- sale windows and presale codes (D17) ------------------------------------------------------------------

    [Fact]
    public void A_phase_sells_inside_its_window_and_a_presale_code_is_compared_by_hash()
    {
        var phase = Phase("p");
        phase.EndsAt = Now.AddDays(1);
        SaleWindows.IsOpen(phase, Now).Should().BeTrue();
        SaleWindows.IsOpen(phase, Now.AddDays(2)).Should().BeFalse();
        SaleWindows.IsOpen(phase, Now.AddDays(-3)).Should().BeFalse();

        var hash = SaleWindows.HashPresaleCode(" fanclub ");
        hash.Should().Be(SaleWindows.HashPresaleCode("FANCLUB"));
        SaleWindows.PresaleAccepted(hash, "FanClub").Should().BeTrue();
        SaleWindows.PresaleAccepted(hash, "other").Should().BeFalse();
        SaleWindows.PresaleAccepted(hash, null).Should().BeFalse();
        SaleWindows.PresaleAccepted(null, null).Should().BeTrue();
    }

    // ---- the item resolver --------------------------------------------------------------------------------------

    private static TicketItemContext Context(TicketItemRef item, TicketType type, IReadOnlyList<TicketEventDate> dates, TicketSalePhase? phase = null,
        SnapshotInventory? seating = null, TicketItemRef? parentRef = null, TicketType? parentType = null, TicketEvent? ev = null)
        => new(item, ev ?? Event(), phase ?? Phase("p", (type.Id!, item.Category, 30m)), type, dates,
            dates.Select(d => d.Id!).Concat(["d-other"]).Distinct().ToList(), seating, Channel(), parentRef, parentType);

    [Fact]
    public void An_admission_on_a_general_admission_date_takes_the_date_quota_and_its_caps()
    {
        var date = Date("d1", Now.AddDays(10));
        var type = Type("t1");
        type.CapacityPerDate = 50;
        type.MinimumAge = 18;
        var phase = Phase("p", ("t1", "", 30m));
        phase.CapacityPerDate = 20;
        phase.QueueEnabled = true;
        var itemId = TicketItemIds.For("p", "t1", "", "d1");

        var item = TicketItemResolver.Resolve(Context(TicketItemIds.Parse(itemId)!, type, [date], phase), new CheckoutItemRequest(itemId, 2), Now)!;

        item.Price.Should().Be(new Money(30m, "EUR"));
        item.Channel.Should().Be("tickets");
        item.OrganizationId.Should().Be("Organizations/seller");
        item.ServiceDate.Should().Be(date.StartsAt);
        item.MinimumAge.Should().Be(18);
        item.AdmissionScopes.Should().Equal(TicketScopes.Admission("p"));
        item.LimitScope.Should().Be("ticketing-event:TicketEvents/1-A");
        item.PerBuyerLimit.Should().Be(6);
        item.Title["en"].Should().Be("Summer Fest · t1");
        item.Capacity.Quotas.Select(q => q.QuotaId).Should().BeEquivalentTo(new[]
        {
            TicketQuotas.TypeOnDate("d1", "t1"), TicketQuotas.PhaseOnDate("d1", "p"), TicketQuotas.Date("d1"),
        });
        item.Capacity.Quotas.Should().OnlyContain(q => q.N == 2);
    }

    [Fact]
    public void Nothing_sells_outside_the_window_unpublished_cancelled_or_without_the_presale_code()
    {
        var date = Date("d1", Now.AddDays(10));
        var type = Type("t1");
        var itemId = TicketItemIds.For("p", "t1", "", "d1");
        var item = TicketItemIds.Parse(itemId)!;
        var request = new CheckoutItemRequest(itemId, 1);

        var closed = Phase("p", ("t1", "", 30m));
        closed.StartsAt = Now.AddDays(1);
        TicketItemResolver.Resolve(Context(item, type, [date], closed), request, Now).Should().BeNull();

        var draft = Event();
        draft.IsPublished = false;
        TicketItemResolver.Resolve(Context(item, type, [date], ev: draft), request, Now).Should().BeNull();

        var cancelled = Date("d1", Now.AddDays(10));
        cancelled.Status = ETicketEventStatus.Cancelled;
        TicketItemResolver.Resolve(Context(item, type, [cancelled]), request, Now).Should().BeNull();

        var started = Date("d1", Now.AddMinutes(-1));
        TicketItemResolver.Resolve(Context(item, type, [started]), request, Now).Should().BeNull();

        var presale = Phase("p", ("t1", "", 30m));
        presale.PresaleCodeHash = SaleWindows.HashPresaleCode("fans");
        TicketItemResolver.Resolve(Context(item, type, [date], presale), request, Now).Should().BeNull();
        TicketItemResolver.Resolve(Context(item, type, [date], presale),
            request with { Options = new Dictionary<string, string> { [TicketOptions.Presale] = "FANS" } }, Now).Should().NotBeNull();

        // A category the phase does not price.
        var other = TicketItemIds.Parse(TicketItemIds.For("p", "t1", "vip", "d1"))!;
        TicketItemResolver.Resolve(Context(other, type, [date], Phase("p", ("t1", "", 30m))), request, Now).Should().BeNull();
    }

    [Fact]
    public void A_pass_takes_a_place_of_each_of_its_dates()
    {
        var dates = new[] { Date("d1", Now.AddDays(10)), Date("d2", Now.AddDays(11)) };
        var pass = Type("pass", ETicketTypeKind.Pass);
        pass.DateIds = ["d1", "d2"];
        var itemId = TicketItemIds.ForPass("p", "pass", "");

        var item = TicketItemResolver.Resolve(Context(TicketItemIds.Parse(itemId)!, pass, dates), new CheckoutItemRequest(itemId, 3), Now)!;

        item.Capacity.Quotas.Select(q => q.QuotaId).Should().BeEquivalentTo(new[] { TicketQuotas.Date("d1"), TicketQuotas.Date("d2") });
        item.ServiceDate.Should().Be(dates[0].StartsAt);

        // A pass bought as a single date is not a ticketing item.
        var single = TicketItemIds.Parse(TicketItemIds.For("p", "pass", "", "d1"))!;
        TicketItemResolver.Resolve(Context(single, pass, [dates[0]]), new CheckoutItemRequest(TicketItemIds.For("p", "pass", "", "d1"), 1), Now).Should().BeNull();
    }

    [Fact]
    public void An_add_on_needs_a_parent_ticket_of_the_same_date_and_takes_only_its_own_cap()
    {
        var date = Date("d1", Now.AddDays(10));
        var parking = Type("parking", ETicketTypeKind.AddOn);
        parking.CapacityPerDate = 40;
        var admission = Type("t1");
        var parentId = TicketItemIds.For("p", "t1", "", "d1");
        var itemId = TicketItemIds.For("p", "parking", "", "d1");
        var item = TicketItemIds.Parse(itemId)!;
        var phase = Phase("p", ("parking", "", 10m), ("t1", "", 30m));

        TicketItemResolver.Resolve(Context(item, parking, [date], phase), new CheckoutItemRequest(itemId, 1), Now).Should().BeNull();

        var resolved = TicketItemResolver.Resolve(Context(item, parking, [date], phase, parentRef: TicketItemIds.Parse(parentId), parentType: admission),
            new CheckoutItemRequest(itemId, 1, ParentItemId: parentId), Now)!;
        resolved.RequiresParentItemIds.Should().Equal(parentId);
        resolved.Capacity.Quotas.Select(q => q.QuotaId).Should().Equal(TicketQuotas.TypeOnDate("d1", "parking"));

        // Another date's ticket, or a parent type the add-on does not allow, is no parent.
        var otherDate = TicketItemIds.Parse(TicketItemIds.For("p", "t1", "", "d2"))!;
        TicketItemResolver.Resolve(Context(item, parking, [date], phase, parentRef: otherDate, parentType: admission),
            new CheckoutItemRequest(itemId, 1, ParentItemId: TicketItemIds.For("p", "t1", "", "d2")), Now).Should().BeNull();
        parking.ParentTypeIds = ["vip"];
        TicketItemResolver.Resolve(Context(item, parking, [date], phase, parentRef: TicketItemIds.Parse(parentId), parentType: admission),
            new CheckoutItemRequest(itemId, 1, ParentItemId: parentId), Now).Should().BeNull();
    }

    [Fact]
    public void A_seated_date_sells_picked_seats_of_the_category_and_keeps_wheelchair_spaces_for_their_types()
    {
        var seating = new SnapshotInventory("snap", LayoutPlanTests.Plan());
        var date = Date("d1", Now.AddDays(10));
        date.LayoutSnapshotId = "snap";
        var type = Type("t1");
        var itemId = TicketItemIds.For("p", "t1", "cat1", "d1");
        var item = TicketItemIds.Parse(itemId)!;

        var picked = TicketItemResolver.Resolve(Context(item, type, [date], seating: seating),
            new CheckoutItemRequest(itemId, 2, Units: ["A-1-3", "snap/A-1-4"]), Now)!;
        picked.Capacity.Picks.Should().ContainSingle();
        picked.Capacity.Picks[0].Picked.Should().BeEquivalentTo(new[] { "snap/A-1-3", "snap/A-1-4" });
        picked.Capacity.Quotas.Should().BeEmpty(); // the seats are the capacity: no general-admission quota

        // Default 3: the wheelchair space needs a type that allows it.
        Action refused = () => TicketItemResolver.Resolve(Context(item, type, [date], seating: seating), new CheckoutItemRequest(itemId, 1, Units: ["A-1-1"]), Now);
        refused.Should().Throw<CheckoutException>();
        type.AllowedSeatTags = SeatTags.Accessible;
        TicketItemResolver.Resolve(Context(item, type, [date], seating: seating), new CheckoutItemRequest(itemId, 1, Units: ["A-1-1"]), Now).Should().NotBeNull();

        // Best available along the chain, and a seat count that does not match the quantity.
        var chained = TicketItemResolver.Resolve(Context(item, type, [date], seating: seating),
            new CheckoutItemRequest(itemId, 3, Options: new Dictionary<string, string> { [TicketOptions.Chain] = "main" }), Now)!;
        chained.Capacity.BestAvailable.Should().ContainSingle();
        chained.Capacity.BestAvailable[0].Should().Be(new ChainLine("snap/chains/main", 3));
        Action mismatch = () => TicketItemResolver.Resolve(Context(item, type, [date], seating: seating), new CheckoutItemRequest(itemId, 3, Units: ["A-1-3"]), Now);
        mismatch.Should().Throw<CheckoutException>();

        // The standing pit has no category: only an uncategorised price sells it.
        var pit = TicketItemIds.Parse(TicketItemIds.For("p", "t1", "", "d1"))!;
        var standing = TicketItemResolver.Resolve(Context(pit, type, [date], seating: seating),
            new CheckoutItemRequest(TicketItemIds.For("p", "t1", "", "d1"), 4, Options: new Dictionary<string, string> { [TicketOptions.Block] = "P" }), Now)!;
        standing.Capacity.Quotas.Should().ContainSingle();
        standing.Capacity.Quotas[0].Should().Be(new QuotaLine("snap/blocks/P", 4));
        Action wrongCategory = () => TicketItemResolver.Resolve(Context(item, type, [date], seating: seating),
            new CheckoutItemRequest(itemId, 1, Options: new Dictionary<string, string> { [TicketOptions.Block] = "P" }), Now);
        wrongCategory.Should().Throw<CheckoutException>();
    }

    // ---- publication -------------------------------------------------------------------------------------------

    [Fact]
    public void Publication_lists_what_prevents_the_event_from_selling()
    {
        var ev = Event();
        ev.Slug = "Not A Slug";
        var date = Date("d1", Now.AddDays(10), capacity: 0);
        var addOn = Type("parking", ETicketTypeKind.AddOn);
        addOn.ParentTypeIds = ["parking"];
        var pass = Type("pass", ETicketTypeKind.Pass);
        pass.DateIds = ["d1"];
        var phase = Phase("p", ("ghost", "", 10m), ("pass", "", -1m));
        phase.PreSelect = true;
        var question = new AttendeeQuestion { Key = "diet", Label = TranslatedString.Of("en", "Diet"), Sensitive = true };
        pass.Questions = [question, new AttendeeQuestion { Key = "diet", Label = TranslatedString.Of("en", "Again") }];

        var problems = TicketEventPublisher.Problems(new TicketEventGraph(ev, [date], [addOn, pass], [phase]), CommerceOptions());

        problems.Should().Contain(p => p.Contains("slug"));
        problems.Should().Contain(p => p.Contains("neither a layout nor a capacity"));
        problems.Should().Contain(p => p.Contains("at least two dates"));
        problems.Should().Contain(p => p.Contains("parent that is not an admission"));
        problems.Should().Contain(p => p.Contains("not of this event"));
        problems.Should().Contain(p => p.Contains("negative price"));
        problems.Should().Contain(p => p.Contains("pre-selects without a waiting room"));
        problems.Should().Contain(p => p.Contains("consent text"));
        problems.Should().Contain(p => p.Contains("used twice"));

        var good = TicketEventPublisher.Problems(new TicketEventGraph(Event(), [Date("d1", Now.AddDays(10))], [Type("t1")], [Phase("p", ("t1", "", 30m))]), CommerceOptions());
        good.Should().BeEmpty();

        var unknown = Event();
        unknown.Channel = "nope";
        TicketEventPublisher.Problems(new TicketEventGraph(unknown, [], [], []), CommerceOptions()).Should().ContainSingle();
    }

    [Fact]
    public void Slugs_are_ascii_lower_case_words()
    {
        TicketEventPublisher.Slugify("Zomerfeest Brüssel 2026!").Should().Be("zomerfeest-brussel-2026");
        TicketEventPublisher.Slugify("   ").Should().Be("event");
        TicketEventPublisher.IsSlug("summer-fest").Should().BeTrue();
        TicketEventPublisher.IsSlug("summer--fest").Should().BeFalse();
        TicketEventPublisher.IsSlug("Summer").Should().BeFalse();
    }

    // ---- attendee questions (GB3) ------------------------------------------------------------------------------

    private static List<AttendeeQuestion> Questions() =>
    [
        new() { Key = "name", Label = TranslatedString.Of("en", "Name"), Required = true },
        new() { Key = "shirt", Label = TranslatedString.Of("en", "Shirt"), Kind = EAttendeeQuestionKind.Choice, Choices = [new() { Key = "s", Label = TranslatedString.Of("en", "S") }, new() { Key = "m", Label = TranslatedString.Of("en", "M") }] },
        new() { Key = "born", Label = TranslatedString.Of("en", "Born"), Kind = EAttendeeQuestionKind.Date },
        new() { Key = "diet", Label = TranslatedString.Of("en", "Diet"), Sensitive = true, ConsentText = TranslatedString.Of("en", "I consent") },
        new() { Key = "later", Label = TranslatedString.Of("en", "Later"), Required = true, AskAt = EAttendeeQuestionTiming.Later },
    ];

    [Fact]
    public void Answers_are_checked_against_their_questions()
    {
        var errors = AttendeeQuestionRules.Validate(Questions(),
        [
            new("shirt", "xl"),
            new("born", "31/12/1990"),
            new("diet", "vegan"),
            new("nope", "x"),
        ], EAttendeeQuestionTiming.Checkout);

        errors.Should().BeEquivalentTo(new[]
        {
            new AttendeeAnswerError("shirt", AttendeeQuestionRules.UnknownChoice),
            new AttendeeAnswerError("born", AttendeeQuestionRules.InvalidDate),
            new AttendeeAnswerError("diet", AttendeeQuestionRules.ConsentRequired),
            new AttendeeAnswerError("nope", AttendeeQuestionRules.UnknownQuestion),
            new AttendeeAnswerError("name", AttendeeQuestionRules.Required),
        });

        // "Later" questions are not required at checkout; a sensitive answer with consent is fine.
        AttendeeQuestionRules.Validate(Questions(), [new("name", "Ann"), new("diet", "vegan", Consent: true), new("born", "1990-12-31")], EAttendeeQuestionTiming.Checkout)
            .Should().BeEmpty();
        AttendeeQuestionRules.Validate(Questions(), [new("name", "Ann")]).Should().ContainSingle();
    }

    [Fact]
    public void Answers_are_stored_normalised_and_consent_is_proven_by_a_hash()
    {
        var multiple = new AttendeeQuestion { Key = "days", Kind = EAttendeeQuestionKind.MultipleChoice };
        AttendeeQuestionRules.Normalize(multiple, " fri, sat ,fri").Should().Be("fri,sat");
        var box = new AttendeeQuestion { Key = "ok", Kind = EAttendeeQuestionKind.Checkbox };
        AttendeeQuestionRules.IsEmpty(box, "false").Should().BeTrue();
        AttendeeQuestionRules.IsEmpty(box, "TRUE").Should().BeFalse();
        AttendeeQuestionRules.ConsentHash("I consent").Length.Should().Be(64);
    }

    // ---- PreSelectOrder (D25) ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_preselection_takes_the_first_alternative_that_fits_with_fewer_tickets_if_needed()
    {
        var lines = new List<PreSelectLine>
        {
            new() { Alternatives = [new() { ItemId = "front", Quantity = 4 }, new() { ItemId = "back", Quantity = 4 }] },
            new() { Alternatives = [new() { ItemId = "closed", Quantity = 1 }, new() { ItemId = "parking", Quantity = 1 }] },
        };
        var attempts = new List<(string, int)>();
        var applied = await PreSelectPlanner.ApplyAsync(lines, (choice, quantity) =>
        {
            attempts.Add((choice.ItemId, quantity));
            return Task.FromResult(choice.ItemId switch
            {
                "front" => new PreSelectAttempt(null, CheckoutRefusals.SoldOut),                                     // never fits
                "back" => quantity <= 2 ? new PreSelectAttempt("line-back", null) : new PreSelectAttempt(null, CheckoutRefusals.LimitExceeded),
                "closed" => new PreSelectAttempt(null, CheckoutRefusals.ItemNotFound),                              // no smaller retry
                _ => new PreSelectAttempt("line-parking", null),
            });
        });

        applied.Should().HaveCount(2);
        applied[0].Alternative.Should().Be(1);
        applied[0].Quantity.Should().Be(2);
        applied[0].BasketLineId.Should().Be("line-back");
        applied[1].Alternative.Should().Be(1);
        attempts.Should().Equal(("front", 4), ("front", 3), ("front", 2), ("front", 1), ("back", 4), ("back", 3), ("back", 2), ("closed", 1), ("parking", 1));
    }

    // ---- .ics (GB9) --------------------------------------------------------------------------------------------

    [Fact]
    public void The_calendar_file_writes_utc_times_the_venue_zone_and_escaped_folded_text()
    {
        var start = new DateTimeOffset(2026, 7, 4, 20, 0, 0, TimeSpan.FromHours(2));
        var ics = EventCalendar.Build([new EventCalendarEntry("d1@ticketing", "Summer Fest; main, stage", start, start.AddHours(3), Now,
            Description: "Doors open: 19:00\nhttps://example.org/events/summer-fest", Location: new string('x', 120), Url: "https://example.org/events/summer-fest",
            VenueTimeZoneId: "Europe/Brussels")]);

        ics.Should().StartWith("BEGIN:VCALENDAR\r\n");
        ics.Should().Contain("DTSTART:20260704T180000Z\r\n");
        ics.Should().Contain("DTEND:20260704T210000Z\r\n");
        ics.Should().Contain("X-WR-TIMEZONE:Europe/Brussels\r\n");
        ics.Should().Contain("SUMMARY:Summer Fest\\; main\\, stage\r\n");
        ics.Should().Contain("Doors open: 19:00\\nhttps://");
        ics.Split("\r\n").Should().OnlyContain(line => System.Text.Encoding.UTF8.GetByteCount(line) <= 75);
        ics.Should().Contain("\r\n x"); // the long location is folded
    }
}
