# Waiting room (`MintPlayer.Spark.WaitingRoom`)

Commerce PRD §4.12, D10, D25, D29; plan M16. A generic package (alias `waiting-room`, ng entry
`@mintplayer/ng-spark/waiting-room`); commerce uses it, nothing in it is commerce-specific.

## Registration

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddWaitingRoom();                  // Spark:WaitingRoom; also registers the risk pipeline (AddChallenges())
    spark.AddChallenges(c => c.UseAltcha()); // optional: a challenge at join (see guide-challenges.md)
});
```

Rooms are created by code (`ISparkWaitingRooms.SaveRoomAsync("summer-fest", new WaitingRoomDefinition { … })`,
e.g. by ticketing per sale phase) or declared in configuration (`Spark:WaitingRoom:Rooms:{key}`, upserted at startup;
configuration beats code). A room is bound to a **gate** (a sale phase id or a route) and has a schedule:
`OpensAt` (pre-queue / registration), `StartsAt` (the sale), `EndsAt`.

## Modes (D10)

| Mode | What happens |
|---|---|
| `RandomizedPreQueue` (default) | Everyone who joins before `StartsAt` is shuffled at the start; later arrivals queue first-come behind them. |
| `Ballot` | Registration until `StartsAt`, then a draw, then staged windows (`Ballot.GroupSize`, `Ballot.WindowLength`); each invited entry gets a personal single-use link valid for its window; unused places roll into the next window. |
| `Fifo` | Ordered by the server's commit time (`@last-modified` at creation, captured once — 0 inversions measured, research §3.6). |

`RandomizedPreQueue` and `Ballot` without sign-in are refused (`SaveRoomAsync` throws, configured rooms refuse startup)
unless `AllowGuestsInFairModes` is set: without sign-in one person is many identities.

**The shuffle (`spark-shuffle-v1`).** A 32-byte seed is created with the room's state and its SHA-256 published as
`SeedCommitment` before joining closes. At the start the entry ids are sorted ordinally, `inputHash = SHA-256(ids
joined by '\n')`, and Fisher–Yates runs on a stream of `HMAC-SHA256(seed ‖ inputHash, counter)` values with rejection
sampling. Afterwards the seed is revealed and `GET /spark/waiting-room/{key}/draw` returns seed, input hash and the sorted
ids, so anyone can recompute the order. Limitation: the operator knows the seed in advance and could add entries; the
published list makes that visible, it does not prevent it.

## One entry per identity

The compare-exchange key `waitingroom/identities/{key}/{identityKey}` (X12) holds the entry id. A refresh, another tab or
another signed-in device returns the same entry; concurrent joins race on the key and exactly one creates the entry.
On sign-in the guest's entry moves to the account (`ISparkVisitorMergeHandler`, X1); if the account already had one the
better is kept (admitted > called > waiting; then the lower rank; then the earlier arrival) and the other is marked left.

Nobody is refused for their network address: the room keys on the identity only, and the risk pipeline's shared-address
signal weighs at most 0.1 (never decisive). The global rate limiter (X10) groups IPv6 by /64 with a high per-address ceiling.

## Admission

The worker (`WaitingRoom.Worker`, a cron job every second) works each room under a lease (`waitingroom/leases/{key}`,
15 s): another instance takes over when the holder dies. Every state change goes through a cluster-wide session, so two
workers that overlap anyway conflict instead of admitting twice. Per tick it shuffles/draws, ranks latecomers (dense
ranks from this single ranker), ends unanswered presence checks and finished admissions, reads the capacity, and calls
the next ranked entries while **active < `MaxActive`**, the **per-minute rate** has room and the **projected capacity**
(`IWaitingRoomCapacity.Remaining − active × UnitsPerAdmission`) is positive.

**Presence check (GA10, WCAG 2.2.1).** A called entry has `PresenceTimeout` (15 s) to answer; the page answers by itself
while visible. A tab that reported itself hidden gets `HiddenPresenceWindow` (default 60 s; startup refuses less than
20 s) and needs one action ("I'm here"). A missed check keeps the rank: the entry is `missed`, and its next sign of life
puts it back as `waiting` at its own rank, so it is called before everyone behind it.

**Admission token.** Data-Protection-sealed, carrying `jti`, room, phase (the gate), entry, subject and expiry
(`Admission.TokenLifetime`, at least hold + payment window). Single use: `IWaitingRoomAdmissions.ConsumeAsync(token,
phase, consumer)` claims `waitingroom/tokens/{jti}` for the consumer; the same consumer is idempotent, any other is
refused (`Spent`) and recorded as deterministic evidence. A visitor's token stays valid after the visitor signs in
(the X1 merge record links them).

### The seam for checkout and hosts

`IWaitingRoomAdmissions` (`IsGatedAsync(phase)`, `ValidateAsync`, `ConsumeAsync`) is what a hold or checkout uses to
refuse requests that did not come through the room. A host route uses
`.RequireWaitingRoomAdmission(http => phase, http => consumer)` (header `X-Spark-Admission`, 403 otherwise). The waiting
room does not reference checkout or inventory: capacity comes in through `IWaitingRoomCapacity` (`[SparkDiscoverable]`),
e.g. answered from `ISparkInventory.GetAvailabilityAsync`.

**The mode type.** `WaitingRoomMode` lives in `MintPlayer.Spark.WaitingRoom.Abstractions` (no dependencies). The
commerce configuration (`Spark:Commerce:Channels:{name}:WaitingRoom:Modes`) and a ticket event's policy name the same
type the room runs, so no mapping exists between them.

### In the commerce suite: `MintPlayer.Spark.Commerce.WaitingRoom`

Checkout, Promotions, Ticketing.Events and the waiting room reference none of each other (PRD §4.1). The bridge package
(alias `commerce-waiting-room`) references all four and plugs in through their extension points:

```csharp
spark.AddCommerceWaitingRoom();   // Spark:Commerce:WaitingRoom; adds the waiting room, Checkout, Promotions, Events
```

| Bridge | Extension point | What it does |
|-|-|-|
| `WaitingRoomCheckoutGate` | Checkout's `ICheckoutAdmissionGate` | Owns the admission scopes `ticketing-phase:{phaseId}` (what Events puts on a queued phase's items) and `waiting-room:{gate}` (any other room). Admits a line when the buyer presents the token the room issued to them for that gate. The consumer is the buyer's identity, so one admission covers every line that person adds while it lives; anyone else is refused as spent (and recorded). |
| `WaitingListQueueState` | Promotions' `IWaitingListQueueState` | A waiting list with `QueueScope = ticketing-phase:{id}` activates once the phase's room is sold out for good or closed. `may-return` is not closed. |
| `TicketPhaseRooms` | `ISparkCommerceWaitingRooms` + after-save interceptors | Every phase with `QueueEnabled` gets the room `RoomKey(phaseId)` (gate = phase id), with the phase's schedule, the event's effective mode (seller's choice clamped to the channel's modes) and sign-in for the fair modes. Saving the phase or its event re-synchronizes it; switching the queue off ends the room. `Spark:Commerce:WaitingRoom:AutomaticPhaseRooms = false` leaves the rooms to the host. |
| `TicketPhaseWaitingRoomCapacity` | `IWaitingRoomCapacity` | Capacity key `ticketing-phase:{id}`: the sum over the phase's dates of the smaller of the date quota and the phase's cap. A seated date answers unknown (the room never sells out by itself; use `MarkSoldOutAsync`). |

**Combined admission (waiting room + presale code).** A checkout item carries `AdmissionScopes`, combined with AND. A
queued phase whose items also need a Promotions presale code carries `ticketing-phase:{id}` and `promotions:{scope}`;
the buyer sends one token per scope (`SparkBasketAddRequest.AdmissionTokens`, `SparkPlaceOrderRequest.AdmissionTokens`).
Checkout asks every gate without consuming first and consumes only when all admit, so a wrong presale code never spends
the waiting-room token. Events' own phase presale code (D17, the `presale` option) is checked when the item resolves.

## Personal data (GDPR)

`AddWaitingRoom()` registers the account hooks of Spark Authorization:

- **Export** (Art. 15/20): the section `waiting-room` lists the account's entries (room, status, rank, pre-queue, arrival,
  last seen, called/admitted times, misses). The admission token is left out: a bearer credential, not data about the
  person.
- **Erasure** (Art. 17): every entry of the account is anonymised in a cluster-wide transaction: the identity key
  (`waitingroom/identities/…`) is released, the user and visitor ids and the token are removed, and an active entry
  leaves the queue. The entry is not deleted: a published shuffle or draw proof lists the ranked entry ids, and removing
  one would make the proof unverifiable; the anonymised entry keeps only its random id, rank and times and expires with
  the room. Evidence the risk pipeline recorded against `user:{id}` is in memory only and lapses after
  `Spark:Challenges:Risk:RecordedEvidenceLifetime`.

## Sold out (truthful)

No remaining capacity and no outstanding holds: the room is `sold-out` for good (joins answer `sold-out`). No remaining
capacity but holds outstanding: `may-return` ("stock may come back"), nobody is called. Capacity again reopens it.
`MarkSoldOutAsync` lets a host decide itself.

## Live updates (X2)

Public topic `waiting-room:{key}`: `{ key, status, servedUpTo, queueLength, seedCommitment, seed }` plus the frame's
`serverTime`. Private stream of the same name per identity: `called` (with the deadline), `admitted` (token, link),
`missed`, `lost`. `IWaitingRoomNotifier` (`[SparkDiscoverable]`) is told the same, for Web Push to a hidden tab or a
ballot link by mail.

## Endpoints

`GET /spark/waiting-room/{key}`, `GET …/entry`, `GET …/draw`, `POST …/join` (200 joined, 428 challenge, 403, 409, 429
with `Retry-After`), `POST …/presence { visible }`, `POST …/leave`, `GET …/priority-access` (a decoy, only with the
deceptive measure on). Writes need antiforgery like every anonymous mutating call with a visitor cookie.

## Accessibility checklist (S12, reduced per round 4: no NVDA/VoiceOver hardware runs)

- [x] No time limit costs the place: missed checks rejoin at the same rank (WCAG 2.2.1 "essential" not needed).
- [x] A hidden tab gets ≥ 20 s for a single action; startup refuses less (2.2.1 "Extend").
- [x] Visible tabs answer by themselves: no action, nothing to miss for keyboard or screen-reader users.
- [x] Status and position are text in `role="status"` / polite live regions, announced at meaningful steps only (4.1.3).
- [x] The turn prompt is a `role="alert"` with one labelled button; the deadline is shown as text, not only a countdown.
- [x] ALTCHA needs no interaction (proof of work); hosted widgets are consent-gated and replaced by ALTCHA on refusal.
- [x] The honeypot is off-screen, `aria-hidden` and `tabindex="-1"`: never reached by keyboard or assistive technology.
- [x] The fairness of the order is explained in words, with a way to check the draw.
- [ ] Manual NVDA/VoiceOver pass and axe in E2E (M18, PRD §14).

## S12 results (reduced per round 4)

Correctness tests in `tests/MintPlayer.Spark.Commerce.Tests/WaitingRoom` and `…/Challenges` (written; the sweep runs in
M22): 0 admission-token double-spends (200 tokens × 10 concurrent consumers), expiry/phase/identity/forgery refusals;
20 tabs + 3 devices = 1 entry; 200 identities behind one IPv4 and 100 in one /64 all joined, 2,000 + 500 identities all
allowed by the risk policy; rank independent of arrival (Spearman |ρ| < 0.1) and uniform first place (χ² test); lease
failover and no double admission. Not run (round 4): Android timings, NVDA/VoiceOver, 200k load, red team, JA4.
