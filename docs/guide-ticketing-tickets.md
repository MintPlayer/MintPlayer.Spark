# Tickets and the door scanner

`MintPlayer.Spark.Commerce.Ticketing.Tickets` (alias `ticketing-tickets`) issues tickets for confirmed ticketing
orders, delivers them, lets holders name, transfer and resell them, and runs the door: check-in lists, a scanner PWA,
and an offline mode. The Angular side is `@mintplayer/ng-spark/ticketing/tickets`. Decisions: `docs/commerce_PRD.md`
§4.11 (D11, D30, GB1, GB3, GB7, GB9, GB12, GC7) and §13 (S8, S9).

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMolliePayments();   // or another provider
    spark.AddMailManager();      // optional: without it, holders open the ticket page instead of a mail
    spark.AddTicketing();        // Ticketing.Events + Ticketing.Venues + Tickets
});
```

```ts
provideRouter([
  { path: 'tickets', children: sparkTicketRoutes() },        // /tickets/{code}, /tickets/transfer/{id}
  { path: 'scanner', children: sparkTicketScannerRoutes() }, // the door
], withComponentInputBinding());
```

Configuration lives under `Spark:Commerce:Ticketing:Tickets` (`SparkTicketingTicketsOptions`). Startup refuses values
that make no sense, for example fewer than 10,000 PBKDF2 iterations or a ticket page path without `{code}`.

## Issuance and delivery

- One ticket per unit of each active ticketing line, issued when Checkout raises `Confirmed`, exactly once per line
  (a `TicketIssuances/{order}/{line}` marker created "must not exist"). Comps and guest-list orders (GB7) issue alike.
- The secret is 128 random bits. The ticket stores only its **PBKDF2-HMAC-SHA256** verifier (16-byte salt, 50,000
  iterations, chosen by spike S8). A Data-Protection-encrypted copy (`TicketSecrets/{code}`) re-renders the page and the
  PDF; it is never a persistent object.
- The QR code is `ST1.{code}.{secret}`. The ticket link is `/tickets/{code}#k={secret}`: the secret is in the fragment,
  which browsers never send to a server, and travels only in POST bodies.
- **Lead time (D11).** `TicketEventSettings.DeliveryLeadTimeHours` holds the page and the mail until that many hours
  before the first date ("available on …"). The delivery job (`Delivery:Schedule`, every minute) mails due tickets,
  grouped per holder and order: one PDF with every ticket (the event's template, GC7) and an `.ics` per date (GB9, from
  Events' iCalendar writer).
- **Templates (GC7).** Per event: accent colours, extra text, a logo and a background (JPEG, uploaded through
  `POST /spark/ticketing/tickets/template-image?eventId=&kind=logo|background`). The QR code, the code, the holder and
  the legal lines keep their place in the library layout.

## Holders: naming, transfer, resale (D30, round 5)

- **Named tickets.** With `NamedTickets`, the buyer names each holder (`POST …/holder`) until the freeze.
- **Transfer.** `POST …/transfer` mails an offer with a token in the link's fragment. With a transfer fee
  (`TransferFeeBasisPoints`, capped by `Exchange:MaxTransferFeeBasisPoints`) the giver pays it through Checkout first.
- **Face-value resale.** `POST …/resale` lists the ticket; the buyer's FinalPrice may not exceed what the ticket cost.
  The listing sells through Checkout as the item `ticket-resale:{id}` over a one-unit quota.
- **Personal data.** On a transfer or resale the new holder's data replaces the previous holder's, which is erased from
  the ticket at once together with the attendee answers of that position (GB3). The ticket is reissued with a new
  secret, so the previous holder's link and PDF stop working. The order keeps the buyer's data (it is the contract);
  the audit trail records only between which accounts or visitor ids the ticket moved.
- **Freeze.** Transfers and resale stop `FreezeBeforeDoorsMinutes` before the first doors (default
  `Exchange:DefaultFreezeBeforeDoors`, 2 hours). Door devices must sync after the freeze.
- Holder data is erased `HolderRetention` (30 days) after the last date, and when the holder deletes their account.

## Back office (GB7) and disputes (GB1)

- Comps, guest lists, re-send, seat change and upgrade are Checkout's back-office operations; tickets follow their order
  events. A seat change or upgrade (`LineChanged`) reissues the line's tickets with new seats and new secrets and
  delivers them again. `POST …/resend` re-sends an order's tickets, or one ticket, for the seller's members.
- A refund revokes the returned units' tickets exactly once per revocation id (I4).
- A dispute freezes the order's unused tickets: they scan as **on hold** ("see the box office"). A won dispute
  restores them; a lost one revokes them. With `RevokeOnDisputeBeforeDecision`, tickets of an event that starts before
  the provider is expected to decide (`Disputes:DecisionHorizon`) are revoked at once instead (restored if won).
- Age-restricted ticket types (GB12) carry their minimum age: the ticket and the scan result show an **ID check**.
  No identity document is stored.

## Check-in lists (GB3)

A `TicketCheckInList` is a gate, a day or a zone: the dates it admits (none = all), the ticket types it admits
(none = all; a zone must name its types) and whether it allows re-entry. An event without lists checks in on one
implicit list. Multi-day and combi passes enter once per date on each list. With re-entry, scans toggle in and out and a
second "in" without an "out" is a double entry; without it, any second entry is one. Double entries are recorded and
flagged, never silently accepted.

## The door scanner

- **Devices.** A member with the `scanner` custom role (or an Admin) registers a device for one event and list
  (`POST …/scanner/devices`); the token is shown once and pasted into the scanner page. Devices authenticate with the
  header `X-Spark-Scanner: {key}.{token}` (stored as a SHA-256 hash), valid until the event's end plus
  `Scanner:TokenGraceAfterEvent`. Revoking a device makes it wipe its list on its next call.
- **Online.** `POST …/scanner/scan` verifies the secret, applies the list's rules and records the presence on the ticket
  under optimistic concurrency: two gates scanning one ticket at once admit it once.
- **Offline (S9).** The device downloads the event's tickets as verifiers (never secrets) in pages of
  `Scanner:ListPageSize` (5,000) and keeps them in IndexedDB (`@mintplayer/ng-spark/offline`), with
  `navigator.storage.persist()` requested. A scan without a connection is verified with WebCrypto PBKDF2 and the same
  rules (`check-in-rules.ts`, the twin of `CheckInRules`), and queued.
- **Sync.** Every 30 seconds, on reconnect, and when the public topic `ticketing-scanner:{eventId}` says the event's
  tickets changed (it carries only a timestamp), the device uploads its queue and downloads the list's changes since its
  cursor. The server recomputes each ticket's presence in time order with every device's scans, so a ticket admitted on
  two offline devices is recorded twice and the later entry is flagged, whichever device syncs first. Re-sending a scan
  id changes nothing.
- **Wipe.** The device deletes its list and queue when revoked, or after the session's `WipeAfter`.
- **Camera.** `@mintplayer/ng-spark/scanning` uses the standard `BarcodeDetector`. It is native only in Chrome on Android,
  macOS and ChromeOS. Elsewhere, register a same-shape ponyfill (self-hosted WebAssembly, lazy-loaded) with
  `setSparkBarcodeDetectorLoader`. Typing the code always works.

## Endpoints (`/spark/ticketing/tickets`)

| Endpoint | Who |
|---|---|
| `GET /mine`, `POST /open`, `/pdf`, `/holder`, `/transfer`, `/transfer/cancel`, `/resale`, `/resale/withdraw` | the holder (secret or holding account) |
| `POST /transfer/offer`, `/transfer/accept` | the recipient (token) |
| `GET /resale/offers?eventId=`, `GET /template-image` | anyone |
| `POST /resend`, `/template-image`, `/scanner/devices`, `/scanner/devices/revoke` | the seller's members (scanner role for devices) |
| `GET /scanner/session`, `GET /scanner/list`, `POST /scanner/scan`, `POST /scanner/sync` | a door device (`X-Spark-Scanner`) |

Refusals answer 409 with `{ code, subject }`, translated as `ticketingTickets.refusal.{code}`.
