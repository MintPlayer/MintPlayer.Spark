# Ticketing: events

`MintPlayer.Spark.Commerce.Ticketing.Events` (alias `ticketing-events`, Angular entry point
`@mintplayer/ng-spark/ticketing/events`) sells tickets for events. The design is in `docs/commerce_PRD.md` §4.11 (D7, D8,
D11, D17, D25, D30, D32), §4.17 (dates and times, D33–D35) and the round-5 items GB2, GB3 and GB9.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMolliePayments();      // or AddStripePayments(), AddTestPayments()
    spark.AddTicketingEvents();     // Spark:Commerce:Ticketing:Events; adds Checkout and Ticketing.Venues
    spark.AddWaitingRoom();         // only for queued phases: it implements Checkout's admission gate
});
```

```json
"Spark": { "Commerce": {
  "Channels": { "tickets": { "Allocation": "HoldFromSelection", "PerBuyerLimit": 8,
                             "WaitingRoom": { "Modes": [ "RandomizedPreQueue", "Fifo" ] } } },
  "Ticketing": { "Events": { "DefaultChannel": "tickets", "EventPagePath": "/events/{slug}" } }
} }
```

## 1. The model: Events → Dates → Sale phases (D17)

Four persistent-object types, each its own document, each owned by the seller's organization (X8):

| Type | Holds |
|---|---|
| `TicketEvent` | name, slug, description, image, channel, status, minimum age (GB12), performers, the per-sale **policy**, attendee-answer retention and deadline. **Publishable** (`IsPublished`). |
| `TicketEventDate` | start, doors, end (shown in the viewer's zone with the venue hint), venue, a seated date's **layout** (frozen as a snapshot at publication) or a general-admission **capacity** |
| `TicketType` | admission, multi-day **pass** (valid on several dates, one place of each) or **add-on** (GB2); dates it applies to, cap per date, minimum age, allowed seat tags (default 3), VAT rate, **attendee questions** (GB3) |
| `TicketSalePhase` | sale window, dates it sells, **prices per ticket type and price category**, cap per date (an early-bird allocation), presale code, waiting-room switch, PreSelect switch |

Dates, types and phases copy their event's organization on every save, so Organizations' row rule applies without a
join. A child never moves to another event.

**Rights.** The library grants `QueryReadEditNewDelete` on the four types to `@authenticated`; the organization row rule
narrows that to the owning organization's members (Viewer reads, Member edits, Admin deletes). **Publishing needs the
Admin role** (`[RequiresOrganizationRole("Publish", Admin)]`). The storefront reads published events through the public
endpoints below; grant `QueryRead/TicketEvent` to `@anonymous` only if you also want them in generic pages.

**Per-sale values (D2).** `TicketEvent.Policy` holds the seller's hold time, per-buyer limit and its scope (the event or
one phase, D16), refund policy, transfer and resale switches (D30), waiting-room mode (D10) and single-seat rule (D7).
They are stored as chosen and clamped to the channel's bounds when read (`SalePolicies.Effective`), so tightening the
configuration applies at once. A seller can lower the channel's per-buyer limit, never raise it.

⚠️ **The per-sale hold time is not applied yet**: Checkout holds every line for its channel's default (`Hold.Clamp(null)`)
and `CheckoutItem` has no hold field. The clamped value is computed and stored; Checkout must accept it.

## 2. Publishing

Setting `IsPublished` (or publishing from the one-screen form) is refused while the event cannot sell:

- no name or an invalid slug (lower-case letters, digits, dashes; unique; made from the name when empty);
- an unknown channel; no date; a date with neither a layout nor a capacity; an unknown venue zone;
- no admission type; a pass on fewer than two dates or on a seated date; an add-on whose parent is not an admission
  or pass of the event; invalid attendee questions (keys, options, a sensitive question without consent text);
- no priced phase; a price for a type of another event, a negative price, two prices for one type and category, prices
  in two currencies; a queued phase in a channel without waiting-room modes; PreSelect without a waiting room;
- the seller may not sell in the channel (`ISparkSellers.CanSellAsync`: approval, KYC, suspension, the DAC7 block D43);
- a legal document the channel requires is not published (GA2, `RequiredLegalDocuments`).

Publishing then **provisions every date** (idempotent):

- a general-admission date gets the quota `ticketing-quotas/{dateId}` with its capacity;
- a seated date freezes its layout version (`ISparkVenues.SnapshotAsync`, scope = the date id), provisions the
  snapshot's chains and block quotas, and publishes its seat states live until the last sale ends. The venue and its zone
  are copied from the snapshot. Every price category a phase uses must exist in the layout;
- a ticket type or phase with a cap per date gets `…/types/{typeId}` or `…/phases/{phaseId}`.

After publication, a new date, type or phase is provisioned when it is saved; a smaller capacity is refused below what is
sold and held; a frozen layout cannot change; a moved start keeps the previous one (schema.org `previousStartDate`). An
event that was ever published, and its children, are never deleted (orders and tickets name them): unpublish the event,
end a phase, or cancel a date.

**One-screen event** (`POST /spark/ticketing/events/quick`): a one-off event with one date, one phase (from now until
the start, unless given) and its ticket types and prices, in the default channel and currency. A member of the
organization may create it; `publish: true` also needs the Admin role.

## 3. Selling through Checkout

Ticket types are a Checkout **capacity source** (PRD §4.1 coupling rule: Checkout never references ticketing). The item
id of a ticket is

```
ticketing:{phaseId}|{ticketTypeId}|{category}|{dateId}      (|pass for a multi-day pass)
```

(`TicketItemIds` in .NET, `ticketItemId()` in TypeScript). The source answers nothing while the event is unpublished or
cancelled, the phase is outside its window, the date is cancelled or has started, the phase does not price the type in
that category, or the presale code is missing or wrong (option `presale`, compared by SHA-256; the phase stores only the
hash: type the code in `PresaleCode`, `-` removes it).

What a line claims:

| Line | Capacity |
|---|---|
| admission, general admission | the date's quota (+ the type's and the phase's caps) |
| pass | each of its dates' quotas (+ caps per date) |
| add-on (GB2) | only its own cap per date; needs `parentLineId` of an admission or pass of the same date (or a pass covering it) and an allowed parent type; Checkout removes it with its parent |
| seated date, `units` | the picked seats (`seatKey` or `{snapshotId}/{seatKey}`), with the single-seat rule; every seat must be of the price category; wheelchair and companion seats only for types whose `AllowedSeatTags` allow them (default 3) |
| seated date, option `chain` | best available along that chain (D25) |
| seated date, option `block` | places in a standing or unnumbered block |

The item carries the FinalPrice basis (gross price, VAT rate), the title "event · type", the service date (the first
date), the minimum age, the per-buyer limit and its scope (`ticketing-event:{id}` or `ticketing-phase:{id}`), and, for a
queued phase, the **admission scope** `ticketing-phase:{phaseId}`: Checkout then claims the line only with a token its
`ICheckoutAdmissionGate` (the waiting room) accepts. One phase is one room, whatever its number of dates.

**Price history (GA3).** Every price is a sellable item `ticketing:{phaseId}|{typeId}|{category}` in Commerce's price
history: a phase save records each changed price with its previous FinalPrice (after the commit, so not in the same
transaction).

## 4. Public pages

| Endpoint (anonymous) | Answers |
|---|---|
| `GET /spark/ticketing/events/public?slug=` | the published event: dates (instants with their offset, the venue zone, address), ticket types, phases with prices and **FinalPrice** |
| `GET …/availability?eventId=&phaseId=` | places left per date and per capped type, sold out, "holds outstanding" (places may come back), and the inventory's live topics `commerce-inventory:{quotaId}` (X2). The waiting page uses it per date |
| `GET …/calendar?dateId=&culture=` | the date as an `.ics` file (GB9) |

**Dates and times (D33–D35).** Every time is stored as `DateTimeOffset` and shown in the viewer's zone. The date's
`StartsAt`, `DoorsOpenAt` and `EndsAt` declare `dateTimeDisplay: { primary: viewer, hint: { zoneFrom: VenueTimeZoneId } }`,
so generic pages show "21:00 (20:00 in London)"; the Angular components use `<spark-date-time [alsoIn]="venueTimeZoneId">`.

**Meta tags, JSON-LD and sitemap (X6, X22, GB9).** The page route (`EventPagePath`, default `/events/{slug}`) gets the
event's title, description and image, and one schema.org `Event` per date, its start written in the venue's offset
(through `SparkTimeZones`, K10), its offers at FinalPrice (`PreSale` before the phase opens), its status, organizer and
performers. Published events are listed in `/sitemap.xml` as `/sitemaps/events/{page}.xml`. `PublicPages: false` turns
both off. The host still enables page meta and the sitemap (`Spark:PageMeta`, `Spark:Sitemap`).

**`.ics` (GB9).** `EventCalendar.Build` writes RFC 5545: start and end in UTC (`…Z`), the venue zone as
`X-WR-TIMEZONE`, the location, the page link, and the doors-open time in the description, formatted by
`ISparkDateTimeFormatter` in the venue's zone. Writing instants in UTC needs no zone conversion (K10). Tickets (M15)
attaches the same file to ticket mails. ⚠️ There is no core `.ics` formatter; the PRD's "generated by the core
formatter" is met by formatting every local time through `ISparkDateTimeFormatter`.

**Marketplace hooks (GA5).** DSA notices on kind `event` resolve to the event's seller; the public event list's ranking
parameters (next date, then name; no paid placement) are published through `IRankingParameterSource`.

## 5. Attendee questions (GB3)

A ticket type lists questions: text, long text, one choice, several choices, date, checkbox (no files). Each is
required or optional and asked **at checkout** (on the order page right after placing) or **later** on the ticket page,
until the deadline (`AnswerDeadlineHours` before the date, default 2 hours).

| Endpoint | Does |
|---|---|
| `GET /spark/ticketing/events/attendees?number=` | the caller's own order: per line, the questions and one answer set per ticket position, what is still missing, the deadline |
| `POST …/attendees` | `{ orderNumber, lineId, position, answers: [{ questionKey, value, consent }], language }` |

Answers are stored per ticket position (`attendee-answers/{orderId}/{lineId}/{position}`). Refused answers come back as
409 with one error per question (`ticketingEvents.answers.{code}`).

- **Sensitive questions** (health, dietary needs) are answered only with explicit consent to the question's consent text:
  the answer stores when consent was given, the language and a SHA-256 of the text shown (GDPR Art. 9(2)(a), Art. 7(1)).
  ⚠️ To be confirmed against EUR-Lex (D37).
- **Export (GB14).** The seller's export kind `attendee-answers` lists one row per answer of its own events; sensitive
  answers are left out unless the question sets `IncludeInExport`.
- **Erasure.** Answers are erased `AnswerRetentionDays` after the event's last date (default 30 days, at most
  `MaxRetention`) by an hourly job; at once when the order is cancelled, expires, is voided, refunded in full or loses a
  dispute; with the buyer's account (GDPR Art. 17); and on a transfer or resale, which Tickets (M15) does through
  `IAttendeeAnswers.EraseAsync`. The buyer's answers are in their personal-data export.

⚠️ Checkout has no hook to refuse "Pay" while required checkout questions are unanswered; the order page asks them right
after placing, and Tickets (M15) can hold back delivery while `Missing` is not empty.

## 6. PreSelectOrder (D25)

On a queued phase with `PreSelect`, a buyer lists what they want while they wait: lines of ranked alternatives (an item,
a quantity, optionally a `chain` or `block`). When the waiting room admits them, the client calls apply with the admission
token: for each line the first alternative that fits is put in the basket, retried with one ticket less while it is short
of places or over the limit ("clamped per line to availability and limits"), so it is held at once in a HoldFromSelection
channel and only pre-filled in an OnPayment one. Release gives every held line back in one click (a line Checkout merged
into one the buyer already had keeps the buyer's own tickets).

| Endpoint | Does |
|---|---|
| `GET /spark/ticketing/events/preselect?phaseId=` | the caller's pre-selection (account or visitor, X1) |
| `POST …/preselect` | saves it (a guest gets the visitor cookie) |
| `POST …/preselect/apply` | `{ phaseId, admissionToken }`: holds it; idempotent while the held lines are in the basket |
| `POST …/preselect/release` | `{ phaseId }` |

The **presence check** is the waiting room's: it issues the token only to a present tab (§4.12). Each line is its own
checkout claim, as every basket line is; the lines are not held in one transaction.

## 7. Angular

```ts
import { SparkTicketingEventComponent, SparkAttendeeAnswersComponent, SparkTicketPreselectComponent } from '@mintplayer/ng-spark/ticketing/events';
```

- `<spark-ticketing-event [slug]>`: the public page; `(seatsRequested)` asks the host to open the seat picker
  (`@mintplayer/ng-spark/ticketing/venues`) for a seated date, then add the line with the picked `units`.
- `<spark-attendee-answers [orderNumber]>`: the questions on the order or ticket page.
- `<spark-ticket-preselect [event] [phaseId] [admissionToken]>`: the pre-selection beside the waiting page.

## 8. Configuration (`Spark:Commerce:Ticketing:Events`)

`DefaultChannel` (`tickets`), `DefaultCurrency`, `EventPagePath`, `PublicPages`, `ShardsPerQuota`, `Answers`
(`DefaultRetention`, `MaxRetention`, `DefaultDeadlineBeforeStart`, `RetentionSchedule`), `PreSelect` (`MaxLines`,
`MaxAlternatives`). Startup refuses an unknown default channel, an unknown currency, a page path without `{slug}` and
nonsensical bounds, and warns when the default channel claims nothing until payment.

## 9. Tests

`tests/MintPlayer.Spark.Commerce.Tests/Events`:

- `EventRulesTests` (pure): item ids, clamped policies, windows and presale codes, the resolver (general admission,
  passes, add-ons, seated dates with picks, chains, blocks and accessible seats), publication problems, slugs, attendee
  answers, the PreSelect planner, the `.ics` writer.
- `EventFlowTests` (RavenDB with the real inventory engine): publication provisions and refuses, selling through the
  capacity source, answers per position with deadline, export, retention and erasure, PreSelect apply and release.
