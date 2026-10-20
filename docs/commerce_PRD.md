# Commerce suite (#521): PRD and plan

**Status:** grilling rounds 1–5 complete (2026-10-17 to 2026-10-19). M0 spikes run per round 3 (desk and CI-size spikes done; the rest run at the start of their milestones; load and cloud spikes dropped in round 4). M1 in progress. ~~Spikes not started.~~ (stale since round 3)
**Issue:** [#521 "Ticket sale platform"](https://github.com/MintPlayer/MintPlayer.Spark/issues/521)
**Companions:**
- [commerce_research.md](commerce_research.md): the evidence, with file:line and sources.
- [commerce_plan.md](commerce_plan.md): the order of work.
- [reference_data_PRD.md](reference_data_PRD.md): the reference-data app (K8). Researched and grilled (RD1–RD8, rounds 1–3); developed on this branch with a basic seed set (D39). ~~Pending its own research and grilling.~~
- [warehouse_picking_PRD.md](warehouse_picking_PRD.md): the warehouse order-picking plugin (D13). Researched and grilled (WD1–WD12, rounds 1–2). ~~Pending its own research and grilling.~~
- The round-5 gap analysis (2026-10-19, session scratchpad `gap-analysis.md`, not committed): A1–A11 legal, B1–B14 commercial, C nice-to-haves, 13 inconsistencies. Its legal sources are **secondary** (law-firm and vendor summaries, Wikipedia); every legal rule is checked against the official EUR-Lex text when it is implemented (D37).

> The decision log (§13) is the record. Where the body and the log disagree, the log wins; fix the body.
>
> **Numbering of round-5 items (D37).** The gap analysis's items keep its letters with a `G` prefix, so each id traces back to one finding: **GA1–GA11** (legal), **GB1–GB14** (commercial), **GC1–GC11** (nice-to-haves, numbered in the analysis's order). They are requirements, not decisions; the round-5 decisions are D37–D40. `G1` (the lookup generator, round 2) is unrelated and keeps its meaning. §4.21 indexes every G-item with its section, package and milestone.

---

## 0. Status

| Item | State | Date | Record |
|---|---|---|---|
| Research: plugin packaging, framework capabilities, RavenDB mechanics, domain, PRD conventions | ✅ | 2026-10-17 | research §1–§5, §10 |
| Research: approvals inventory, bot mitigation, persistent-object name identity | ✅ | 2026-10-17 | research §6, §8, §9 |
| Grilling round 1 (Q1–Q32 + defaults) | ✅ | 2026-10-17 | §13 Round 1 |
| Grilling round 2 (Q33–Q36, K10–K12, G1) | ✅ | 2026-10-17 | §13 Round 2 |
| Reference-data app: research, grilling, PRD | ✅ (~~⏳~~, stale until round 5) | 2026-10-17 | K8; Q-O6 closed; `reference_data_PRD.md` |
| Reference-data app: development + basic seed set on this branch | ⏳ | — | D39; M19 |
| Warehouse plugin: research, grilling, PRD | ✅ (~~⏳~~, stale until round 5) | 2026-10-17 | D13; Q-O7 closed; `warehouse_picking_PRD.md` |
| Spikes S0–S24 | ◐ desk and CI-size spikes done (S0, S1 at CI size, S3, S8, S10, S11, S16–S19, S21, S23, S24); S2/S4 and the full S1 dropped (round 4); the rest run at their milestone | 2026-10-19 | §7; §13 Rounds 3–4 |
| Round 4: no stress tests, no paid resources | ✅ | 2026-10-19 | §13 Round 4 |
| Round 5: gap analysis in scope (GA1–GA11, GB1–GB14, GC1–GC11), fuzzy by default, reference-data seed | ✅ decided; ⏳ to build | 2026-10-19 | §13 Round 5; §4.19–§4.21 |
| Milestones M0–M22 | ⏳ (M1 largely done, per plan) | — | §10, plan |

---

## 1. Goal

Ship **modular Spark libraries** that let a downstream developer host a webshop, a ticket-sale platform, or both, with a minimum of code. **Configuration alone** decides how stock is allocated:

- **Webshop:** a product is sold only if it is available at the moment the payment completes.
- **Ticketing:** seats are held for N minutes while the buyer pays, become occupied on payment, and are released when the hold expires.

Both run on one reservation engine. They differ in configuration values and in which packages the host references (D1, D2).

### Owner constraints

| # | Constraint |
|---|---|
| K1 | Modular: many libraries, each with a concise purpose. |
| K2 | Webshop versus ticketing behaviour is chosen by .NET configuration, not by different code. |
| K3 | No backward compatibility: the libraries are in preview. Core behaviour may change without shims. |
| K4 | "Work on this as long as you want; I want to ship a good plugin/product." |
| K5 | One PR for everything, including every defect found along the way and the companion PRDs (D13, K8). |
| K6 | This repo is a **framework**, not a platform. Every platform policy is a configurable building block; the demo app shows a platform. |
| K7 | The framework does not pick vendors. It ships several adapters (payment providers, carriers, e-invoicing access points, SMS, human-verification challenges) and the host chooses in configuration. |
| K8 | A new **reference-data app** (countries, languages, administrative levels, places with merger and split history, streets; localized and romanized; public typeahead API; deterministic seeding). It has its own PRD; commerce consumes its addresses (§4.18). ~~Live-deployed~~: **not deployed by us**. Owner: "i don't think I'll be deploying this reference data app to my vps. If the code is written and can work properly, that would be great already". Any host can run its own instance. **Round 5 (D39):** developed on this branch, with a **basic seed set** (some countries, regions, places, streets) so it can be tested; owner: "All of that is in-scope for the current branch". |
| K9 | Redesigns are allowed, including of production data and deployment. Every VPS change they imply is listed in §8. |
| K10 | **Time zones are handled in one place.** All time-zone logic lives in core-framework built-ins: `IRequestTimeZoneResolver`, X14 recipient locale, X16 `<spark-date-time>` and `ISparkDateTimeFormatter`. Libraries and apps never convert or format zones themselves. This is enforced (X16). *(Row moved above K11 in round 5; it used to follow K12.)* |
| K11 | **Standard web platform APIs first.** Whatever a W3C/WHATWG browser API can do is done with it, not with a library or server round trip. Where support is partial, use a polyfill that implements the *standard* API shape, so it can be dropped later, and document a browser-support matrix per feature. Examples in this PRD:<br>• `Intl.DateTimeFormat` (X16) and `Intl.NumberFormat` (money, X4);<br>• `Intl.RelativeTimeFormat` (countdowns), `Intl.Collator` and `String.prototype.normalize` (accent-insensitive client filtering);<br>• `Intl.DisplayNames` (localized country, language, script and currency names);<br>• WebCrypto PBKDF2 (tickets), WebAuthn (passkeys);<br>• Service Worker + Cache Storage (scanner PWA), Push API + Notifications (queue turn);<br>• `BroadcastChannel` (one live socket shared by all tabs, and one queue entry across tabs, D29), Page Visibility (presence check, D25);<br>• Screen Wake Lock (scanner), Web Share (event links), `BarcodeDetector` (native where present, WASM ponyfill with the same API otherwise). |
| K12 | **Prefer source generators over hand-written boilerplate.** Wherever a generator significantly reduces the code we write (registration, options binding, contracts, constants, typed clients), use one, incremental. Code fixes live in the generator assembly that raises their diagnostic, never in a separate `.CodeFixes` assembly (owner, 2026-10-05; `MintPlayer.Spark.SourceGenerators.csproj:53-54`). Where a Roslyn generator can't do the job (TypeScript output, data tables shared by C# and TS), a build-time tool with a CI diff check does it instead. The study's recommendations are folded into §5. |

### Non-goals

See §9. In short: no wallet passes, no resale above face value, no stored-value gift cards, no season tickets or dynamic pricing, no Angular SSR, and no physical warehouse operations inside commerce (the warehouse plugin owns those).

---

## 2. What Spark already gives us

The full table, with verdicts, is in research §1. These are the parts the design leans on:

| Need | Spark feature | Evidence |
|---|---|---|
| Live push to browsers | `/spark/live` WebSocket; `SparkLiveService` reconnect + resubscribe | `libs/spark/MintPlayer.Spark/Endpoints/Live/LiveSocket.cs:23-75`; `libs/node_packages/ng-spark/services/src/spark-live.service.ts:51-74` |
| Writes made outside Spark trigger a push | `ISparkLiveNotifier.Changed/CollectionChanged` | `ISparkLiveNotifier.cs:19-27` |
| Atomic hold plus its expiry message | Messaging outbox `IMessageOutbox.EnqueueAsync(session, …)` | `libs/messaging/MintPlayer.Spark.Messaging/IMessageOutbox.cs:19` |
| Backstop sweeper with second precision | Cron (6-field), one run per occurrence across the cluster | `libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:243,270` |
| Expired documents hidden at read time | `IExpiring` + `ExpirationRowPolicy` | `IExpiring.cs:34`; `ExpirationRowPolicy.cs:30` |
| Reservation pattern | Compare-exchange create-only put, takeover after expiry, release guarded by owner | `UserStore.cs:94-123,699-751`; `MessagingLease.cs:75-97` |
| Atomic decrement pattern | OCC retry loop (20 concurrent → exactly 5 admitted) | `libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:46-77` |
| Seller tenancy | Organizations: `IOrganizationOwned`, roles | `libs/organizations/MintPlayer.Spark.Organizations/IOrganizationOwned.cs:25,46` |
| Order lines | AsDetail + `[ValueKey]`, per-row 409 merge | `docs/guide-asdetail-attributes.md:178-254`; `docs/guide-concurrency.md:136-148` |
| Step-up flows (payment redirect, challenge) | 449 retry + client methods | `docs/guide-manager-retry-actions.md:243-312` |
| Sale start and end times | DateTimeOffset end to end, `{Name}Raw` companions | `docs/guide-dates-and-sorting.md:16,39,236-290` |
| QR codes | `SparkQrCodeRenderer` (SVG) | `SparkQrCodeRenderer.cs:17` |
| Cross-app data (warehouse, reference data) | `[Replicated]` ETL + SyncAction write-back. ⚠️ ETL is unavailable on the Community licence production runs (§4.9) | `docs/guide-cross-module-sync.md`; `docs/endpoints_generator_adoption_plan.md:219` |
| Plugin packaging | `AddX(this ISparkBuilder)`, library layers, slots, `AddIndexesFrom`, Registry endpoints | research §2; `libs/notifications/MintPlayer.Spark.Notifications` (template) |

Gaps closed in this PR are listed in §5 (framework additions) and §6 (defects).

---

## 3. Product scope

### 3.1 Personas

- **Platform host:** the developer or operator. Writes configuration and a handful of registration lines.
- **Seller:** the staff of a seller organization (D21). Organization roles, plus `scanner` and `finance` (X8).
- **Buyer:** a guest with a visitor identity (X1) or an account. Signing in can be required per channel (D4).
- **Door staff:** the scanner PWA (D11).
- **Platform admin / approver:** venue approvals (D8), seller approvals (D21), refund oversight.

### 3.2 Scenarios the libraries must serve with configuration only

| Scenario | Merchant (D5) | Channels (D2) | Waiting room |
|---|---|---|---|
| Single-merchant webshop | `Platform` | `shop: OnPayment` | optional (drops) |
| Ticket marketplace | `ConnectedSellers` | `tickets: HoldFromSelection` | yes |
| Festival: tickets and merchandise in one app | either | `tickets` + `shop`, **one basket and one payment** (`Basket:MixChannels`, D40/GB2) | for tickets |

The demo app (D22) runs the festival scenario, with both systems hooked up. A ticket plus a T-shirt is one order and one payment (D40).

### 3.3 What a host writes

Illustrative only. The final API is fixed in milestones M4–M17.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<FestivalContext>();
    spark.AddAuthentication<SparkUser>();
    spark.AddMessaging();
    spark.AddOrganizations();
    spark.AddCommerce();                         // channels, sellers, fees: Spark:Commerce
    spark.AddCommercePayments().UseMollie().UseStripe();
    spark.AddShop();                             // products, variants, shipping selection
    spark.AddShipping().UseSendcloud();
    spark.AddInvoicing().UsePeppol<SomeAccessPoint>();
    spark.AddTicketing();                        // events, venues, tickets, scanner
    spark.AddWaitingRoom().UseAltcha();
});
```

```jsonc
"Spark": {
  "Commerce": {
    "Merchant": "ConnectedSellers",                 // D5
    "Sellers": { "Registration": "ApprovalRequired" },  // D21
    "Channels": {
      "tickets": {
        "Allocation": "HoldFromSelection",          // D1
        "Hold": { "Default": "00:10:00", "Min": "00:05:00", "Max": "00:20:00" },  // D2 seller bounds
        "MaxPaymentWindow": "00:30:00",             // D3
        "RequireSignIn": true,                      // D4
        "WaitingRoom": { "Modes": [ "RandomizedPreQueue", "Ballot", "Fifo" ], "Default": "RandomizedPreQueue" }  // D10
      },
      "shop": { "Allocation": "OnPayment", "RequireSignIn": false }
    },
    "Basket": { "MixChannels": true }               // D40 (GB2): festival = one basket, one payment
  }
}
```

Client side: one `provideSparkCommerce(...)` provider plus route features (`withShopRoutes()`, `withTicketingRoutes()`, `withWaitingRoomRoutes()`). Every page can be replaced per route (D27).

---

## 4. Architecture

### 4.1 Package map (D31)

The folder names under `libs/` stay short so paths stay under MAX_PATH. The project names are fully qualified. Aliases are **permanent**.

| Package | Alias | Purpose | Depends on | ng entry point |
|---|---|---|---|---|
| `MintPlayer.Spark.Commerce.Abstractions` | — | Interfaces that app entities implement; `Money` re-export; channel/policy enums | Abstractions | — |
| `MintPlayer.Spark.Commerce` | `commerce` | Channels and bounds, seller profiles and registration, fees, price display, audit. **Round 5:** `FinalPrice` and the price history (GA3), versioned legal documents (GA2), trader traceability, ranking transparency and P2B terms (GA5), DSA notice-and-action on listings (GA5, through Moderation), DAC7 seller data (GA6), seller data-access roles and exports (GB14) | Organizations, Approvals, Moderation (round 5, GA5) | `commerce/core` |
| `MintPlayer.Spark.Commerce.Inventory` | `commerce-inventory` | Quotas, claims, sharding, numbered units, chain-fill allocator, per-buyer limits, sweeper, live availability | Commerce | — |
| `MintPlayer.Spark.Commerce.Payments` | `commerce-payments` | Provider interface, merchant modes and Connect onboarding, method catalog, webhooks, idempotency. **Round 5:** dispute/chargeback events (GB1), settlement and payout reconciliation (GB4), no refusal by card-issuing country (GA11) | Commerce, Webhooks | `commerce/payments` |
| `….Payments.Mollie` / `.Stripe` / `.Test` | — | Adapters (D6) | Payments | — |
| `MintPlayer.Spark.Commerce.Checkout` | `commerce-checkout` | Baskets, orders, allocation policies, payment orchestration, refunds and cancellations, linked-identifier limits. **Round 5:** multi-channel baskets (GB2/D40), terms acceptance and the "order with obligation to pay" button (GA2), the withdrawal function and its receipt (GA1), the dispute state (GB1), back-office orders (GB7), age attestation (GB12), geo-blocking checks (GA11), abandoned-basket reminders (GC1), donations (GC3), insurance (GC4) | Inventory, Payments | `commerce/checkout` |
| `MintPlayer.Spark.Commerce.Promotions` | `commerce-promotions` | Codes, presale access, capacity-blocking codes, waiting list. **Round 5:** seller sale prices with the Omnibus prior price (GA3), back-in-stock notifications (GB11), tracking links (GC2) | Checkout | `commerce/promotions` |
| `MintPlayer.Spark.Commerce.Invoicing` | `commerce-invoicing` | Numbering, receipts, invoices, credit notes, VAT categories, VIES, e-invoice export. **Round 5:** place of supply, reverse charge, VAT territories, OSS export (GA4), DAC7 report (GA6), accounting export and fee ledger (GB4), IOSS (GC8), optional TEDB rates (GC10), retention floors (§4.15) | Checkout | `commerce/invoicing` |
| `….Invoicing.Peppol.<Provider>` (×2) / `.Test` | — | Peppol access-point adapters (D19, Q-O2) | Invoicing | — |
| `MintPlayer.Spark.Commerce.Shipping` | `commerce-shipping` | Carriers, labels, tracking, `IShippingRateProvider`. **No Checkout dependency**, because the warehouse's pack station uses it too. The checkout shipping step lives in Shop | core, Webhooks | `commerce/shipping` |
| `….Shipping.Sendcloud` / `.Bpost` / `.PostNL` / `.Test` | — | Carrier adapters (D14). **Round 5:** service points and lockers, return labels, multi-parcel (GB5), customs documents (GB6), dangerous-goods service restrictions (GC9) | Shipping | — |
| `MintPlayer.Spark.Commerce.Fulfilment` | `commerce-fulfilment` | Shop side of the fulfilment contract (D13). The contract itself goes in `MintPlayer.Spark.Fulfilment.Contracts` and the transport is chosen in the warehouse PRD (push + reconcile proposed; no ETL on Community) | Checkout, Fulfilment.Contracts | — |
| `MintPlayer.Spark.Commerce.Shop` | `commerce-shop` | Products, variants (with SKU and GTIN), categories, the checkout shipping-method step, returns and 14-day withdrawal, storefront. **Round 5:** unit price (GA3), GPSR product information (GA7), legal-guarantee claims and return reasons (GA8), HS code and origin (GB6), media and facets (GB13), age flag (GB12), customer account area (GB10), reviews (GC5), product feeds (GC6) | Checkout, Shipping, Fulfilment | `commerce/shop` |
| `MintPlayer.Spark.Commerce.Ticketing.Events` | `ticketing-events` | Events → Dates → Sale phases, ticket types, prices, sale policies. **Round 5:** add-on ticket types (GB2), attendee questions (GB3) | Checkout | `ticketing/events` |
| `MintPlayer.Spark.Commerce.Ticketing.Venues` | `ticketing-venues` | Venues, layouts, editor, snapshots, chain order, seat tags, CSV | Inventory, Approvals | `ticketing/venues` |
| `MintPlayer.Spark.Commerce.Ticketing.Tickets` | `ticketing-tickets` | Issuance, delivery lead time, named tickets, transfer, face-value resale, scanner PWA. **Round 5:** check-in lists, multi-day passes, re-entry and zones (GB3), comps, guest lists, re-send and seat change (GB7), dispute freeze (GB1), door ID check (GB12), per-event PDF templates (GC7), `.ics` (GB9) | Events, Checkout | `ticketing/tickets` |
| `MintPlayer.Spark.Approvals` | `approvals` | Decision requests + document proposals (D9, D26) | core | `approvals` |
| `MintPlayer.Spark.WaitingRoom` | `waiting-room` | Rooms, modes, admission tokens, presence check, live position | core (+ Challenges, optional) | `waiting-room` |
| `MintPlayer.Spark.Challenges` (+ `.Turnstile`, `.HCaptcha`, `.Recaptcha`, `.FriendlyCaptcha`) | `challenges` | `ISparkHumanChallenge` (ALTCHA built in), risk-signal pipeline, deceptive measures (D29). **Round 5:** third-party adapters declare that they need consent; startup refuses them without the consent primitive wired (GA9, X21) | core | `challenges` |
| `MintPlayer.Spark.Sms` (+ adapters, Q-O1) | — | SMS sending for phone verification (D29) | core | — |
| `MintPlayer.Spark.Webhooks` | — | Generic signed or fetched webhooks, raw-body preservation, dedupe; GitHub webhooks re-pointed onto it | Messaging | — |

**Reference sets:**
- **Webshop host:** Commerce, Inventory, Payments (+ a provider), Checkout, Shop, Shipping (+ a carrier), Fulfilment, Invoicing (+ an access point). Optionally Promotions and WaitingRoom (drops).
- **Ticketing host:** Commerce, Inventory, Payments, Checkout, Ticketing.Events / Venues / Tickets, WaitingRoom, Challenges, Promotions, Invoicing.
- **Festival:** the union.

**Round-5 core additions** (no new package): the consent primitive (GA9 → X21, core + `@mintplayer/ng-spark/consent`), discovery output beyond meta tags (GB9 → X22) and the embed mode (GB8 → X23). See §5.

**Coupling rule.** Checkout never references Promotions, Invoicing, Shipping, Fulfilment or Ticketing. Those plug in through Checkout's extension points: pricing steps, order-event recipients, capacity sources. A webshop therefore carries no seat-map indexes or rights (memory: *don't couple unrelated features*).

### 4.2 Configuration model

- **Channels (D2).** Named under `Spark:Commerce:Channels:{name}`. Each has an allocation mode (D1), hold bounds, `MaxPaymentWindow` (D3), `RequireSignIn` (D4), waiting-room modes (D10), refund-policy bounds (D20), fees (default 4), resale switches (D30), and the deceptive-measures switches (D29). Every sellable item belongs to exactly one channel. ~~A basket never mixes channels.~~ *(Superseded by D40, round 5: it contradicted the festival scenario, §3.2.)* A basket holds one channel by default; with `Basket:MixChannels` it holds lines of several channels (below).
- **Multi-channel basket (D40, GB2).** Off by default; the festival host turns it on. The rules:
  - **Each line keeps its channel's rules:** allocation mode, hold bounds, per-buyer limits, waiting-room admission, refund policy, fees, resale switches. A ticket line is held from selection while a T-shirt line in an `OnPayment` channel claims nothing until payment.
  - **One order, one payment.** The payment window is the **shortest** `MaxPaymentWindow` among the basket's held lines; the methods offered are those every held line allows (D3). The basket's countdown shows the earliest hold deadline.
  - **Sign-in** is required when any line's channel requires it (D4). A waiting-room-gated line needs its admission token; other lines do not.
  - **Partial failure.** When an `OnPayment` line loses the race at confirmation, `Basket:OnLineFailure` decides: `RefundLine` (default: the rest of the order is confirmed and that line is refunded, with a credit note) or `VoidOrder` (everything is voided or refunded). Held lines cannot lose: their units are already claimed.
  - **Exactly once per line.** Every return path returns each line's counters to its own quotas (I4).
  - **Sellers.** In `ConnectedSellers` mode a mixed basket may span sellers only when `Basket:MixSellers=true` **and** the provider adapter reports split capability (`supportsSplit` in the method catalog: Stripe separate charges and transfers, Mollie split payments/routing, both to be confirmed against their public docs, D37). ~~Startup refuses `MixSellers` with a provider that lacks it.~~ *(Superseded by D41, round 6:)* `MixSellers` also needs the host's opt-in `Basket:AcknowledgeSplitMerchantOfRecord=true`, because both split mechanisms route the money through the platform, which is then the merchant of record for that order (licensing/KYC consequences, see the guide); startup refuses `MixSellers` when the opt-in is off or no registered provider can split, and a split payment is only ever created on a provider that can. Off by default. Each seller remains the issuer of its own lines' invoices (§4.8).
  - **Add-ons inside the tickets channel** (pretix's add-on categories, research §5.1): an `AddOn` ticket type (parking, camping, lockers, merchandise vouchers) is sold only together with a parent ticket in the same order and is refunded with it. This works without `MixChannels`.
- **Per-sale values.** Sellers choose values per sale or product inside the channel bounds (D2). Stored values are clamped against the bounds when read, so tightening the configuration takes effect immediately.
- **App-wide settings.** `Merchant` (D5), `Sellers:Registration` (D21), currency rules (D19), and the challenge provider plus its runtime switch points (D29, via `SparkSetting<T>`).
- **Startup validation.** Through `Registry.AddMiddleware`, in the Notifications pattern (`SparkNotificationsExtensions.cs:95,102-121`). Startup **refuses**:
  - `Sellers:Registration=Open` with `Merchant=Platform`, unless `AcknowledgeOpenPlatformMerchant=true` (D21);
  - a `RandomizedPreQueue` or `Ballot` mode in a channel without `RequireSignIn`, unless overridden (D10);
  - a deceptive measure enabled without its explicit trigger setting (D29);
  - **round 5:** a consent-requiring adapter (third-party challenges, analytics, marketing mail) without the consent primitive wired (GA9, X21);
  - **round 5:** ~~`Basket:MixSellers=true` with a provider that cannot split payments (D40);~~ *(round 6, D41:)* `Basket:MixSellers=true` without `Basket:AcknowledgeSplitMerchantOfRecord=true`, or when no registered provider can split payments;
  - **round 5:** a risk signal or shipping/payment rule keyed on the buyer's country of residence, nationality or card-issuing country in a way the Geo-blocking Regulation forbids (GA11). Country-keyed **delivery zones** stay allowed; the check validates configuration, it does not judge custom code (docs note).

  It **warns** on a per-buyer limit in a channel that allows guests (D4), and (round 5) on a shop channel with no legal documents published (GA2) and on a host without an accessibility statement (GA10).
- **Precedence.** Configuration beats code. This is Moderation's order (`SparkModerationExtensions.cs:44-49`), chosen because configuration is meant to pick the scenario (K2). Values that decide what gets registered are bound eagerly, as in IdentityProvider (`SparkIdentityProviderExtensions.cs:52`).

### 4.3 Inventory engine (D1, D7, D15, D16, D25, D29)

**Invariants.** Every test and spike checks these:

- **I1 — No oversell.** For every quota *q*, at every instant, `sold(q) + activeHolds(q) ≤ capacity(q)`. `activeHolds` counts only holds with `expiresAt > now` (lazy expiry).
- **I2 — All or nothing.** A claim succeeds or fails as a whole, across every quota shard, seat key, per-buyer key and code quota it touches. It runs as **one cluster-wide transaction**: atomic guards on documents plus compare-exchange values. That is correct on a single node and on a cluster (research §3).
- **I3 — The sweeper is not needed for correctness.** It refreshes displayed availability, prunes, and triggers side effects. Killing it may leave availability stale, never oversold.
- **I4 — Exactly once.** Every release returns each counter exactly once, keyed by claim id (idempotent). The same applies to every return path: expiry, payment failure, refund, cancellation, resale, late payment after an extension (D16), **withdrawal (GA1)**, a **refused line in a mixed basket (D40)** and a **dispute lost (GB1, round 5; it was missing from this list)**.
  - **Dispute lost.** A lost chargeback releases what is still releasable: unused tickets are revoked and their units return, exactly once, keyed by the dispute id; shipped goods follow the exception below. A dispute that is **won** returns nothing, and a frozen ticket is unfrozen.
  - **Exception for shipped physical goods.** A refund of units **already shipped** returns no quota. Those units become sellable again only when they are **restocked**: the return-disposition step in the shop, or the warehouse's restock event when a warehouse app backs the stock. Units refunded before shipment are released normally.
  - Per-buyer counts are returned on refund in both cases.
  - **Withdrawal timing (GA1, CRD Art. 13).** For a withdrawal from shipped goods, the refund may be **withheld until the goods are received back or the buyer shows proof of dispatch, whichever is earlier** (Art. 13(3), as summarised by the research; confirm against EUR-Lex, D37). That is §4.9's "refund on receipt" setting, with proof of dispatch added as a second trigger. The quota still returns only at restock, never at refund.
  - *Evidence:* the warehouse research found that returning quota on refund double-counts once the warehouse restocks (scratchpad r13 §4; carried into `warehouse_picking_PRD.md`).
- **I5 — Grace.** An expired hold can be taken over only after `expiresAt + grace`. A payer converting their own hold gets the grace, to absorb clock skew (research §3).

**Shapes.** Initial; spikes S1–S3 validate or replace them.

| Thing | Representation |
|---|---|
| Quota | `quotas/{id}`: capacity, shard count K, mappings (sellable item → quota, with a multiplier for bundles) |
| Quota shard | `quotas/{id}/shards/{k}`: capacity share, `sold`, `holds[{claimId, n, expiresAt}]`. Expired entries are pruned in the same write that claims, so expiry stays lazy even with counters |
| Numbered unit (seat) | Compare-exchange `seats/{dateId}/{seatId}` → `{claimId, expiresAt \| null}`. Taken over after expiry (UserStore pattern, `UserStore.cs:699-734`) |
| Per-buyer limit | Compare-exchange `limits/{scope}/{kind}/{hash}` → `{count, holds[…]}`. One key per linked identifier: account, visitor, normalised e-mail, address, card fingerprint (D29) |
| Claim | `claims/{id}`: owner (basket/order), lines (item, n, units), state, expiresAt |

**Operations.** All are idempotent by claim id and retried on concurrency conflicts, falling back to another shard:
- `TryClaim(lines, owner, expiresAt)`
- `Adjust` (HoldFromSelection quantity change)
- `Extend` (PaymentPending, D3)
- `Convert` (final: expiresAt = null)
- `Release`

**Seat assignment (D7, D25).**
- **Best available is chain fill.** The organiser defines a fill order across blocks and within each block (row order, direction). A group receives the next contiguous run in chain order, taking the smallest hole that fits before the cursor run. The cursor is a hot key: S2 measures its contention, and the lever is one chain per block group. This is close to the range-reservation pattern measured at 0.14 ms per item (`SequenceSourceSpikeTests.cs:137-152`).
- **Pick your seat.** The client proposes seats. The server enforces the "no single empty seat in a row" rule (configurable) before claiming.
- **Unnumbered blocks.** Standing or seated without numbers. These are quota-only.

**Live availability.** After each commit the engine publishes availability per date, block and quota to broadcast topics (X2).

**Sweeper.** A cron job every few seconds:
- releases expired holds (lazily, the same way a claim does);
- publishes availability;
- cancels the provider payments of expired PaymentPending orders;
- triggers notifications and the waiting list (D18).

### 4.4 Checkout and the order lifecycle (D1, D3, D20)

```
Basket ──(HoldFromSelection: every line change claims or adjusts holds)──┐
   │                                                                      │
   └─ Pay ─▶ Order[PaymentPending]
               • HoldFromSelection: Extend holds to the PSP expiry for the method, capped by MaxPaymentWindow (D3)
               • HoldFromCheckout:  TryClaim now, same expiry rule
               • OnPayment:         soft availability check only, nothing claimed
             ├─ paid / authorized ─▶ Convert ─▶ Confirmed
             │     • hold lapsed (late payment) → re-claim the SAME units → Confirmed | Refunding → Refunded
             │     • OnPayment → claim+convert atomically → Confirmed | Voiding/Refunding → Voided/Refunded (D1)
             ├─ failed / expired / cancelled ─▶ Release ─▶ Cancelled (basket kept while its own holds live)
Confirmed ─▶ TicketsIssued / FulfilmentRequested / Invoiced
          ─▶ CancellationRequested (refund policy, D20) ─▶ Refunded | PartiallyRefunded
          ─▶ EventCancelled ─▶ MassRefund (D20)
          ─▶ WithdrawalRequested (GA1, unconditional) ─▶ [goods back or proof of dispatch] ─▶ Refunded | PartiallyRefunded
          ─▶ GuaranteeClaim (GA8) ─▶ Repaired | Replaced | PriceReduced | Refunded
          ─▶ Disputed (GB1: tickets frozen) ─▶ DisputeWon (unfrozen) | DisputeLost (tickets revoked, I4)
```

The comps, guest-list and phone orders of GB7 enter this diagram at `Confirmed` (zero total) or at `PaymentPending` (payment link). They are back-office orders, not box-office sales; door sales stay Q-O4.

- **Zero-total orders** (free tickets) confirm without a provider (D6).
- **The provider webhook is the source of truth**; the redirect is for the user only. Confirmation is idempotent: a duplicate webhook is a no-op, and a second payment for the same order is refunded (research §4).
- **The buyer cancels at the provider:** the provider payment is cancelled actively and the holds are released at once (D3).
- **Waiting-room-gated phases:** `TryClaim` requires a valid admission token (§4.12).
- **Linked-identifier limits (D16, D29).** Account, visitor, e-mail and address are checked at claim. The card fingerprint is checked at payment:
  - Stripe exposes it before confirmation, so the order is refused before any charge;
  - Mollie exposes it only after payment, so the order is voided or refunded within 60 s (S12).
- **Prices** shown always include every mandatory fee (default 4). Fees are broken down at checkout.
- **`FinalPrice`, defined once (round 5; default 4 never defined it).** `FinalPrice = unit price + VAT + every mandatory fee and charge the buyer cannot avoid` (service, booking and platform fees; not optional extras, not delivery when the buyer chooses the method). It is computed in one place (Commerce, `IFinalPriceCalculator`) and used by: every displayed price (default 4); the face-value resale cap (D30; the Belgian rule caps resale at the price paid including mandatory costs, research §7.2, confirm against the Act of 30 July 2013, D37); the Omnibus prior price (GA3), which compares `FinalPrice` values; and promotions' "was/now" display.
- **Pre-contract information and the order button (GA2, CRD Art. 6 and 8).** The last checkout step shows the main characteristics, the total `FinalPrice` with delivery, the seller's identity (GA5), and links to the current terms, privacy policy and withdrawal information (§4.20). The buyer accepts the terms (versions stored on the order). The pay button reads **"Order with obligation to pay"** (translated; the text is a library translation key the host may reword only to an equally unambiguous text). Confirmation goes out on a durable medium (mail with the terms and withdrawal information attached as PDF, Q-O9).
- **Geo-blocking (GA11, Reg. 2018/302).** A buyer from any member state can buy on the same conditions: no refusal or redirect based on nationality, residence or the card's issuing country. Delivery zones remain the seller's choice; a buyer outside them can still buy with collection or delivery inside them. Tickets (services at a location) use the same conditions regardless of residence. Risk signals (§4.12) may use the country only as deterministic evidence (e.g. a card fingerprint reused across identities), never as a refusal criterion on its own.
- **Back-office orders (GB7).** Staff with the `box-office` organization role create orders without the buyer's checkout: phone and manual orders (payment link mailed, or marked paid by an authorised `finance` member with an audit entry), **comps and guest lists** (zero-total orders, D6, issued from a named list; they count against quotas and per-buyer limits like any order; the `Comp` reason is shown in reports and excluded from revenue), re-sending tickets and confirmations, and **changing a seat or upgrading** (a release + claim in one cluster-wide transaction, I2, with the price difference charged or refunded).
- **Age-restricted items (GB12).** An item can carry `MinimumAge`. The basket shows it; checkout requires an attestation ("I am at least 18"), stored on the order; the ticket or pickup slip carries an "ID check" marker that the scanner (§4.11) and the warehouse handover (if any) show. No ID document is stored.
- **Abandoned-basket reminder (GC1).** Opt-in per channel, only for a buyer with marketing consent or an account preference (GA9); one mail after a configurable delay; never for held seats (a hold expiring is not a reminder trigger).
- **Donations and round-up (GC3).** A pricing step adds a donation line (fixed amounts or round-up to a whole unit) to a host-configured beneficiary; VAT category "outside scope" by default (host configurable); refunded only with the whole order.
- **Ticket insurance (GC4).** An `IInsuranceProvider` adapter (+ `.Test`) offers a cancellation cover as an optional line; the insurer, not the seller, refunds claims. Shown only after the main price, never pre-ticked (UCPD; confirm, D37).

### 4.5 Payments (D3, D5, D6, D29)

- **Provider interface:**
  - create a payment with method, expiry, capture mode and merchant context;
  - get, cancel, refund (amount + idempotency key), capture, void;
  - parse a webhook into a provider event;
  - a method catalog with `maxExpiry`, `supportsAuthorization` and `fingerprintBeforeConfirm`.
- **Expiry cap.** Methods whose provider expiry exceeds the channel's `MaxPaymentWindow` are not offered in held channels (D3). Bank transfer, for example: Mollie gives it 12 days, against iDEAL 15 min, card 30 min, Bancontact 1 h and PayPal 6 h (research §4).
- **Mollie.** Webhooks carry only an id; the payment is fetched with the right merchant credentials, so there is no signature to check. Connect is OAuth plus application fees.
- **Stripe.** Webhooks are signed (`Stripe-Signature` with a timestamp tolerance). Checkout Sessions expire after at least 30 min, longer than a 10-minute hold, so held flows use an explicit expiry or PaymentIntents, decided in S5. Connect uses connected accounts plus `application_fee_amount`.
- **Test provider.** In-process pages simulate success, failure, provider expiry, late and duplicate webhooks, partial refunds and refund failure. E2E tests and the demo run on it.
- **Merchant modes (D5):**
  - **`Platform`:** the app's own provider account.
  - **`ConnectedSellers`:** each seller links their own account through the provider's onboarding. Selling is blocked until the provider reports the seller verified (D21). *(Round 6, D41: the one exception to "never holds third-party funds" is an opted-in mixed-seller order, which is split through the platform; §4.2.)* A platform fee is configurable. Refunds come from the seller's account, and a "refund failed: insufficient balance" state is handled (D20).
- **Fixtures.** Real adapters are tested against **recorded real responses**: no live provider calls in CI (memory: *parsers need real fixtures*). Sandbox keys are set by the owner in user-secrets and never seen by the assistant. *(Round 4: until the owner's development accounts exist, the fixtures come from the providers' public documentation, marked as such. Round 5 (D37) keeps this for every new adapter feature below.)*
- **Disputes and chargebacks (GB1).** The provider interface gains a dispute event: Stripe `charge.dispute.created/updated/closed`, Mollie chargebacks (fetched per payment). On a dispute:
  - the order goes to **Disputed**; its unused tickets are **frozen** (they scan as "on hold, see the box office") until the dispute closes, or **revoked** at once when the event starts before the provider's decision is due (seller setting);
  - an **evidence pack** is assembled from the audit trail, the decision log, the delivery or scan records, the accepted terms version (GA2) and the IP/visitor history, for the seller to submit (Stripe accepts evidence by API; Mollie through its dashboard: the pack is then a download);
  - the seller is notified (mail + notification); in `ConnectedSellers` mode the provider debits the seller's account and the platform records it in the fee ledger (GB4);
  - *(round 6, D42:)* a Mollie chargeback has no provider decision: one not reversed after `Disputes:ChargebackFinalAfter` (default **120 days**, the card-scheme dispute window) counts as lost; the seller may accept it as lost earlier;
  - **lost:** I4 returns what is releasable (dispute id as idempotency key), a credit note is issued (§4.8), and the buyer's linked identifiers get a configurable cooldown (D29); **won:** tickets are unfrozen.
- **Settlement reconciliation (GB4).** An `ISettlementSource` per adapter imports the provider's settlement or payout report (Stripe balance transactions and payouts, Mollie settlements) and matches each line to an order, refund, dispute or fee. Unmatched lines and amount differences land in a reconciliation queue for the `finance` role. A **platform-fee ledger** records every application fee, refund of a fee and dispute debit per seller.
- **Card country (GA11).** No method is hidden and no payment refused because of the card's issuing country. Prepaid-card refusal (D29) is method-based, not country-based.

### 4.6 Promotions and waiting list (D18)

- **Codes.** Effects are: percentage off, fixed amount off, unlock hidden items, and **block capacity** (a quota held by the code). Codes have usage limits, validity windows and once-per-buyer rules. A presale access code is a code that opens a sale phase (D17).
- **Discounts on partial refunds.** A discount is spread pro rata across order lines, which defines both partial refunds and VAT bases (D19).
- **Waiting list.** Capacity freed after a sell-out is offered to the next person in line as a personal, time-limited code (e.g. 24 h). The list activates only when the phase is sold out, its queue has closed, and no holds are outstanding; until then, freed capacity goes to the queue. *(Round 5: this activation rule is the **ticket mode**. It is ticket-specific, while the scenario table also uses Promotions for shops; shops use the back-in-stock mode below, GB11.)*
- **Back-in-stock notification (GB11).** The shop mode of the waiting list: a buyer (account or verified e-mail; no marketing consent needed, it is a requested service mail) subscribes to a variant; when its available capacity rises above zero, subscribers are mailed in subscription order, in batches sized to the restocked quantity × a configurable factor. No code and no reservation: the first payer wins (`OnPayment`). Subscriptions expire after a configurable period and are erased with the account (GDPR hooks).
- **Seller sale prices and the Omnibus prior price (GA3, PID Art. 6a).** A seller can set a **sale price** with a validity window per item or variant (a price change, not a code). Every price change is appended to a **price history** per sellable item (the money audit, default 8, extended with the `FinalPrice` before and after). Whenever the storefront shows a reduction (a strike-through, "−20 %", "was/now", a sale badge), it renders automatically the **lowest `FinalPrice` of the previous 30 days** before the reduction as the reference price; a reduction cannot be displayed without it. Progressive reductions keep the reference of the first step; perishable goods may be exempted by the host per item (member-state option; confirm per country, D37). Codes entered by the buyer (personal discounts) are not announced reductions and need no prior price (confirm against the Commission guidance, D37).
- **Tracking links (GC2).** A seller creates promoter or affiliate links (`?ref=` plus UTM parameters); the visitor's first attribution within a configurable window is stored on the basket and the order; per-link sales reports; optional commission rules feed the fee ledger (GB4). Stored only with analytics consent where the host's consent policy requires it (GA9).

### 4.7 Refunds and cancellations (D20)

- **Seller refund policy.** Set per sale or product within channel bounds: none, until X days before, or with a fee.
- **Mandatory flows:**
  - **Event cancelled:** a **mass refund**, batched, resumable, idempotent and showing progress. It respects provider rate limits and handles connected-seller balance failures with retry, notification and a manual fallback.
  - **Event rescheduled:** a keep-or-refund window.
  - **Goods:** the 14-day withdrawal right. Dated leisure services are exempt under CRD Art. 16(l) (research §7).
- **Withdrawal is unconditional (GA1; round 5 fixes the text).** The seller refund policy above governs **cancellations the seller allows**; it never governs the statutory withdrawal from goods, which needs no reason and no approval. The flow:
  - **The withdrawal function (CRD Art. 11a, inserted by Directive (EU) 2023/2673, applying from 19 June 2026 per the research; confirm the date and wording against EUR-Lex, D37).** On every order page with a live withdrawal right (signed-in, or a guest through the signed order link, D4, X1) a permanently available, clearly labelled button ("Withdraw from contract here", a translation key) opens a short form (which order lines; optional reason, never required), then a **confirmation step**, then an **acknowledgement on a durable medium** (mail with a PDF) stating the date and time the withdrawal was received. The function stays visible until the withdrawal period ends, counted from delivery per line.
  - **The model withdrawal form** (CRD Annex I(B)) is published with the legal documents (§4.20) and accepted by mail too: staff can register a withdrawal received by mail or phone with its receipt time.
  - **No approval step.** The request is recorded as `WithdrawalRequested` at once. Staff can only process it (return label, receipt), never refuse it; a request outside the period or for an exempt line is shown as such to the buyer before confirming, and staff see the reason.
  - **The refund** covers the price **and the original standard delivery cost** (not the surcharge for a more expensive delivery the buyer chose), within 14 days of the withdrawal, by the original payment method (CRD Art. 13(1)–(2), confirm, D37). It may be **withheld until the goods are back or proof of dispatch is shown**, whichever is earlier (Art. 13(3)); this is the I4 interaction in §4.3 and the "refund on receipt" setting in §4.9. Return shipping is the buyer's unless the seller chose to bear it (a seller setting, shown in the withdrawal information).
  - **Diminished value** (handling beyond what is needed to establish the nature of the goods) can be deducted by staff with a reason and an audit entry, after receipt.
- **Legal guarantee claims (GA8, Directive 2019/771).** Distinct from withdrawal: a non-conformity claim within the legal guarantee period (2 years by default; host-configurable per country, confirm per country, D37) opens a **guarantee claim**: repair, replacement, price reduction or refund (termination), in the hierarchy the directive sets. The seller pays the return shipping. A claim is not a withdrawal: no 14-day clock, no unconditional refund.
- **Return reasons (GA8).** Every return carries a reason from a taxonomy: `Withdrawal`, `NonConformity` (guarantee), `WrongItem`, `DamagedInTransit`, `SellerAllowed` (goodwill, policy above), `Other`. The reason picks the flow, the refund rules (delivery cost refunded or not) and the warehouse disposition default (Restock for withdrawal, Inspect → Quarantine for non-conformity). It travels in `expected-returns` (warehouse §4.3).
- **Every refund** produces a credit note (D19), returns quota and per-buyer counts exactly once (I4), and revokes the affected tickets (§4.11).
- **Fees.** Whether service and platform fees are refunded is a channel setting. A withdrawal refund ignores that setting for the delivery cost (above).

### 4.8 Invoicing (D19)

- **Numbering.** Gap-free per issuer, allocated atomically (compare-exchange counter per issuer per year).
- **Documents.** B2C receipts, B2B invoices and credit notes, as PDF.
- **Issuer.** The seller in `ConnectedSellers` mode, the platform in `Platform` mode. The platform also invoices sellers for its fees.
- **VAT.** Categories and rates are defined by the host per country; nothing is hard-coded. B2B buyers' VAT ids are checked through VIES. *(Round 5: this line omitted place of supply and reverse charge, although D19 and research §5.4, §5.5 item 25 list them. Added below as GA4.)*
- **Place of supply (GA4).** An `IPlaceOfSupplyRule` per line picks *which country's* rate applies (confirm each rule against the VAT Directive 2006/112/EC on EUR-Lex, D37):
  - **B2C goods shipped within the EU:** the destination country once the seller's EU-wide distance sales exceed the €10,000 threshold (or by the seller's option below it), else the seller's country. The seller's running total is tracked per calendar year;
  - **admission to events:** the country where the event takes place (research §7.6), for B2C and B2B;
  - **B2B goods and services between member states:** **reverse charge**: 0 % with the mention "Reverse charge" (Art. 196) on the invoice, given a valid VIES-checked VAT id;
  - **VAT territories:** a host-maintained exception list (Canary Islands, Åland, Mount Athos, Northern Ireland as `XI` for goods, …) treated as third territories or special cases; the list is data, seeded from the reference data's country profile (D39) and editable;
  - **exports outside the EU:** 0 % with proof of export (customs, GB6).
- **OSS return export (GA4).** A quarterly export per seller of B2C distance sales and admission services by member state of consumption and rate (the Union OSS return layout), as CSV, for the seller to file. IOSS (imports ≤ €150, **GC8**) is the same export for non-EU sellers shipping into the EU, with the IOSS number on the order and the customs data (GB6).
- **EU VAT rates (GC10).** An optional reference-data source imports the Commission's TEDB rates per member state into a **suggestion** table; the host still approves its own rates (nothing is applied automatically; liability below).
- **DAC7 report (GA6, Directive 2021/514).** In `ConnectedSellers` mode with goods (and any service the host declares "personal service"), the platform collects per seller (GA5 data plus TIN, date of birth for individuals, bank account), checks the TIN format (and the EU TIN service where available), aggregates consideration, fees and number of transactions **per quarter**, and exports the yearly report (the DAC7 XML schema of the reporting member state, plus a CSV) by 31 January. Ticket sales are flagged "probably out of scope" until confirmed (D37). Sellers excluded by the directive (e.g. fewer than 30 sales and under €2,000 a year) are marked, not dropped.
- **Accounting export (GB4).** Per seller and period: a sales journal (orders, credit notes, VAT per rate), a payments journal (from reconciliation, §4.5) and a UBL batch of the period's invoices; CSV with a documented column set, so accountants can map it.
- **Retention (round 5, inconsistency 5).** Invoices, credit notes and accounting records are kept for the **legal minimum per document type and country** (Belgium: 10 years for accounting records and invoices per the research; **confirm per country**, D37), whatever shorter period the host sets for personal data. They are never altered by anonymisation (§4.15): personal data on an issued invoice stays as issued until the floor expires.
- **E-invoicing.** An abstraction producing EN 16931 / Peppol BIS (UBL), with access-point adapters chosen by the host (Q-O2) plus a test adapter. In Belgium this is mandatory for B2B since 2026-01-01 (research §7).
- **Currency.** One currency per seller, and one currency per order.
- **Liability.** The rules are configurable. Defaults are never presented as tax advice.

### 4.9 Shipping, carriers, fulfilment (D12, D13, D14)

- **Shipping methods** come from configuration: a flat rate per zone, a free-shipping threshold, and pickup. `IShippingRateProvider` covers real rate calculation.
- **Service points and parcel lockers (GB5).** An `IServicePointProvider` per carrier adapter (bpost Pick-up points, PostNL locations, Sendcloud service-point API; fixtures from public docs, D37) lets checkout show the nearest points for the shipping address on a list (and a map where the host enables one); the chosen point id travels on the order and the label.
- **Return labels and multi-parcel (GB5).** In shop-only mode (no warehouse), staff can create **several parcels** per order (each with its own label and tracking) and a **return label** for a withdrawal or guarantee claim, mailed to the buyer. With the warehouse, packing does both (warehouse §4.7).
- **Customs (GB6).** Products carry an **HS code** and **country of origin** (Shop). Shipments outside the EU customs territory (UK, CH, NO, …) get a **CN22/CN23** or commercial invoice through the carrier adapter, and the host chooses **DAP** (buyer pays duties) or **DDP** (seller pays) per destination zone; checkout shows the choice to the buyer.
- **Dangerous goods (GC9).** Items flagged with a UN number or a lithium-battery class (warehouse `WarehouseItem`, or the shop item without a warehouse) restrict the carrier services offered; a service that does not accept the class is never offered, and labels carry the required marks where the carrier API takes them.
- **Holiday calendars (GC11).** Carrier cut-offs and delivery promises ("delivered by Friday") skip public holidays from the reference data's optional holiday calendar per country (reference_data_PRD §4.4), through the core zone-aware schedule (warehouse WX14, K10).
- **Carrier adapters:** Sendcloud, bpost, PostNL and a test carrier. They produce labels and receive tracking webhooks (through the generic webhooks, X5). A small shop prints labels itself; with the warehouse app, labels are printed at packing.
- **Fulfilment contract.** A `FulfilmentOrder` plus shipment status, kept generic (lines, quantities, addresses, shipments). It is proven with a stub consumer, and the warehouse plugin (D13) is its first real consumer. Stock levels flowing back from the warehouse are part of the warehouse PRD.
  - **Package split.** The warehouse research proposes a separate tiny `MintPlayer.Spark.Fulfilment.Contracts`, so the warehouse never references Checkout. `Commerce.Fulfilment` would then implement the shop side over it. The warehouse PRD decides.
  - **Stock feed.** The warehouse publishes, per SKU and channel, `OnHandSellable`, `ReservedOtherChannels`, a cumulative `ShippedTotal` and a `Version`. The shop's capacity source sets `capacity = OnHandSellable − ReservedOtherChannels − buffer + ShippedTotal`, which keeps I1 exact even when updates arrive late, out of order or twice. To be validated in the warehouse PRD.
  - **Cancellation** is a status change with an accept/reject handshake, never a delete.
  - **Fulfilment-order ids are unique across shops**, because one warehouse may serve several shops.
  - **Returns for warehouse-backed stock:** the refund waits for the warehouse's receipt of the goods. This is configurable: refund on receipt, or on request. *(Round 5, GA1: for a withdrawal, "on receipt" means on receipt **or on the buyer's proof of dispatch, whichever is earlier** (CRD Art. 13(3)); staff register the proof on the return. The 14-day refund deadline runs from the withdrawal regardless.)*
  - **Push mechanics (WD1):**
    - versioned contracts (C# projections) declared in configuration, granted as `Replicate/{contract}`;
    - a version stamped on each save, with the outbox entry stored through `IAfterSaveCommitted` in the save's own commit;
    - the push handled by messaging;
    - newer-version-wins read-only replicas with tombstones, and `IReplicaChanged<T>` to derive state transitions.

    This needs no ETL licence, no extra subscriptions and no cross-database certificates.
- **Transport (decided in the warehouse grilling, WD1 + WD2; see `warehouse_picking_PRD.md`).** RavenDB Community has no ETL (`docs/endpoints_generator_adoption_plan.md:219`), and production runs Community 7.1.10.
  - Spark Replication picks its transport **by licence capability**: **ETL when the licence allows it**.
  - Otherwise a module that wants to change data it doesn't own sends a **SyncAction request to the owning module**. The owner applies it and **pushes** the new state over HTTPS to every consumer module, and a cron **reconcile by digest** covers the initial load and repairs missed pushes.
  - All cross-module calls authenticate with **module client certificates**.
  - For shop ↔ warehouse, state flows **both ways** with **one owner per fact**, and neither app writes the other's documents (WD2).

### 4.10 Shop (D12)

- **Catalogue.** Products with optional variants (size, colour), each variant with its own quota; simple categories for navigation; search through Spark search (`docs/guide-search.md`), typo-tolerant by default (D38).
- **Order states.** Paid → Shipped (with tracking) → Delivered.
- **Returns.** ~~A buyer refund request the seller approves. The 14-day withdrawal right is built in.~~ *(Superseded in round 5: withdrawal from goods is unconditional, so it can never be a request the seller approves.)* Three flows, picked by the return reason (§4.7): the **unconditional withdrawal** through the withdrawal function (GA1); a **legal-guarantee claim** (GA8); and a **seller-allowed return** (goodwill, within the seller's policy, which the seller may approve or refuse).
- **Unit price (GA3, PID).** Items sold by quantity carry a unit of measure and net quantity (kg, l, m, m², piece count); the storefront shows the **unit price** (per kg, per litre, …) next to the `FinalPrice`, computed, never typed.
- **GPSR product information (GA7, Reg. 2023/988 Art. 19).** A `ProductSafety` attribute group on products (a model attribute group, so it renders in the generic screens): manufacturer name, postal and electronic address; the EU responsible person when the manufacturer is outside the EU; a product identifier (GTIN, model, batch) and a picture; warnings and safety information **per language** (`TranslatedString`, X17). The storefront product page shows it in a fixed block; a product with goods and no manufacturer data cannot be published (refused at publish, not at startup).
- **Legal-guarantee information (GA8, ECGT Directive 2024/825, applying from 27 Sep 2026 per the research).** Product pages and the order confirmation show the legal guarantee notice (a host-translatable text, harmonised notice once the Commission publishes it, confirm, D37), and any **commercial durability guarantee** the producer gives, as a field. Seller texts are not scanned for generic environmental claims; the seller guide documents the ban (host matter).
- **Customs fields (GB6).** HS code and country of origin per product or variant.
- **Media and facets (GB13).** A gallery per product with per-variant images (alt text required, translatable); facet filters on category and on declared attributes (size, colour, brand, price range), using Spark search plus distinct values (memory: distincts are not facets; facet counts come from a dedicated index map, X19).
- **Age flag (GB12).** `MinimumAge` per product (§4.4).
- **Reviews (GC5).** Optional. Only buyers of a delivered order line can review it ("verified purchase"); the storefront states **whether and how reviews are verified** (UCPD Annex I points 23b/23c as inserted by the Omnibus directive, confirm, D37); reviews are moderated through Moderation; no paid or incentivised review without disclosure.
- **Product feeds (GC6).** A Google Merchant and Meta catalogue feed export (XML/CSV) per seller, generated from published products with `FinalPrice`, availability, GTIN and images; cached and served at a stable URL.
- **Storefront pages** follow §4.14.

### 4.11 Ticketing (D7, D8, D11, D17, D25, D30, D32; default 3)

- **Events → Dates → Sale phases (D17).**
  - A **Date** has its own layout snapshot, quotas, doors-open time and start time.
  - A **phase** has its own sale window, offered ticket types and prices, an optional presale access code, queue settings, and the PreSelect switch.
  - A one-off event is a single screen with defaults.
  - A phase that covers several dates has one waiting room, and its waiting page shows availability per date.
- **Venues and layouts (D8, D32):**
  - A **Venue** (name, address from reference data, §4.18) is a shared catalogue entry.
  - A **Layout** belongs to its creator and can be used by others as is, or copied to edit.
  - Creating and modifying shared venues and layouts goes through **Approvals**. Modifications always need approval; creations need it when configured.
  - A published date freezes a layout **snapshot**.
  - **Editor:** polygon blocks over an optional uploaded background; seat generation (rows × seats; straight, angled or curved; labelling schemes); standing polygons with capacity; decorative shapes; chain order by dragging; seat tags; price categories; CSV seat import and export; zoom, pan, undo, redo, snapping, touch and keyboard. It renders to canvas with level of detail (S7).
  - **Accessible seating (default 3).** Seat tags; ticket types restricted to tagged seats; a wheelchair ticket can hold the adjacent companion seat.
- **PreSelectOrder (D25).** A pre-selection is ticket type × block or price category × quantity, with ranked alternatives. At admission the server holds it in one atomic step, clamped per line to availability and limits, after a **presence check**. Releasing it is one click. In OnPayment channels it only pre-fills.
- **Tickets (D11, D30, default 9):**
  - One ticket per unit, with a 128-bit random secret stored as **PBKDF2-HMAC-SHA256**. PBKDF2 is native in .NET and in browser WebCrypto, so the offline scanner verifies without extra libraries. The cost is tuned in S8 to the gate peak.
  - The QR code encodes ticket id + secret.
  - The ticket page and an e-mailed PDF are delivered on the **seller's chosen lead time**; until then the buyer sees "available on …".
  - Optional named tickets, transfer (with a name change, free or with a fee), and an **official face-value resale** exchange. A resold ticket is reissued with a new secret. Transfers and resale **freeze** a configurable time before doors open. The resale cap is the original `FinalPrice` (§4.4).
  - **Personal data on transfer and resale (round 5, inconsistency 10).** The ticket holder and the buyer are separate data subjects. On a transfer or resale, the new holder's name and answers (GB3) replace the previous holder's on the ticket, and the previous holder's personal data is **erased from the ticket** at once; the previous holder loses access to the ticket page (new secret). The **order** keeps the buyer's data (it is the contract and the invoice basis, §4.8 retention). The seller sees the current holder only; the audit trail records that a transfer happened and between which accounts or visitor ids, not the erased answers.
  - **Per-event PDF templates (GC7).** The ticket PDF and page use a template chosen per event (logo, colours, background image, extra text), within a library layout that keeps the QR code, the secret and the legal texts fixed.
  - **Add to calendar (GB9).** Ticket mails carry an `.ics` attachment (start, doors, venue address, the ticket page link), with times in UTC plus the venue's `TZID` (K10: generated by the core formatter, not by hand).
- **Attendee questions (GB3).** Per ticket type, the seller defines questions (text, choice, date, consent checkbox, file excluded), required or optional, asked at checkout per position or later on the ticket page until a deadline. Answers are stored per ticket position, exportable to the seller (GB14), and erased after a seller-set retention (default: 30 days after the last date), through the GDPR hooks. Questions marked "sensitive" (health, dietary) need explicit consent text (GA9) and are excluded from exports by default.
- **Check-in rules (GB3).** A **check-in list** per gate, day or zone selects which ticket types and dates it admits. Multi-day and combi passes are valid on several Dates; each Date's list counts one entry per pass. **Re-entry**: a list may allow check-out/check-in (a pass toggles in/out; a second "in" without an "out" is a double entry). **Zones** (VIP, backstage) are lists that admit only tagged ticket types. Offline lists (below) carry the list id, so devices enforce the same rules.
- **Back-office ticket work (GB7)** follows §4.4: comps, guest lists, re-send, seat change and upgrade.
- **Disputes (GB1).** A disputed order's tickets scan as "on hold" (§4.5); a lost dispute revokes them.
- **Scanner PWA.**
  - It is built on the **shared** `ng-spark/scanning` and `ng-spark/offline` entry points that the warehouse PRD introduces, so tickets and warehouse use one scanning and offline stack. Camera scanning needs a self-hosted WebAssembly `BarcodeDetector` polyfill, because the native API exists only in Chrome on Android, macOS and ChromeOS (warehouse research).
  - The `scanner` organization role uses an event-scoped device token.
  - Online scans mark tickets used atomically, **per check-in list** (GB3).
  - Tickets of age-restricted types (GB12) show an "ID check" banner on the scan result; frozen tickets (GB1) show "on hold".
  - Offline mode works from a hashed ticket list, then syncs and flags double entries. The list is revocable and wiped after the event, and scanners must sync after the freeze.

### 4.12 Waiting room and bot defence (D10, D25, D29)

- **Room.** Bound to a gate (a sale phase, or any route) with a schedule: pre-queue opens, sale starts, sale ends.
- **Modes (D10):**
  - **`RandomizedPreQueue`** (default). At the start, entries are shuffled with a recorded seed; later arrivals queue FIFO behind them.
  - **`Ballot`.** Registration, then a provably fair draw (seed committed before registration closes, revealed after), then personal single-use purchase links in staged windows; unused places roll to the next group.
  - **`Fifo`.** Ordered by server time, using `@last-modified`, which gave 0 inversions in measurement (`docs/issue_460_PRD.md:1662-1669`).
- **One entry per identity.** A compare-exchange key per room and identity. A refresh, extra tabs and other devices all return the same entry.
- **Position.** Ranks are dense. A single ranker assigns latecomers' ranks about once per second, so there is no hot sequence key. Clients compute `rank − servedUpTo` from a **public broadcast topic** carrying `{servedUpTo, queueLength, status, serverTime}` (X2). A private stream per identity delivers the admission.
- **Admission.** One admission worker per room, holding a lease. It admits while:
  - active shoppers are below the maximum;
  - the per-minute rate has room;
  - projected remaining capacity exceeds outstanding holds.

  The **presence check** gives an admitted tab a few seconds to answer; if it doesn't, the place passes on and the visitor rejoins. Admission is a right to try, not a reservation (research §3).

  *Round 5 (GA10, WCAG 2.2.1):* the presence check answers itself while the tab is visible (no user action needed); when the tab is hidden it notifies (Push/Notifications) and waits at least 20 s for a single action. A visitor who misses it rejoins at **their previous rank**, not at the back, so the time limit never costs the place. The queue countdown itself is informational, not a limit on the user. The hold timer is in §4.14.
- **Admission token.** Signed (Spark Data Protection) and single-use, with the spent marker under lazy expiry. It is bound to identity, room and phase. Its time to live is at least the hold plus the payment window. **Hold and checkout APIs refuse requests without it.**
- **Sold out.** The room closes truthfully when no capacity and no outstanding holds remain. While holds are still outstanding, the page says stock may come back.
- **Bot defence (D29):**
  - **Defaults:** the admission chain; one entry per identity; posted and enforced linked-identifier limits; one hold per identity with a growing cooldown for repeated unpaid holds; identity-keyed rate limits with IPv6 /64 grouping (X10); a decision log; truthful messages.
  - **Opt-ins:**
    - *(round 5, GA9)* the third-party challenge adapters (Turnstile, hCaptcha, reCAPTCHA, Friendly Captcha) collect device signals, so each declares `RequiresConsent`; startup refuses them unless the consent primitive (X21) is wired, and a visitor who refuses consent gets ALTCHA (no third party) instead of being blocked. Research §7.3 cites the CNIL fines;
    - challenge providers (`UseAltcha()` default, `UseTurnstile()`, `UseHCaptcha()`, `UseRecaptcha()`, `UseFriendlyCaptcha()`, `UseChallenge<T>()`), applied at queue join during the pre-queue, as a step-up at hold through the 449 retry, and never at admission; at payment, 3-D Secure is requested instead;
    - challenge points switchable **at runtime**;
    - phone verification with SMS adapters (Q-O1); a minimum account age (X11); refusing prepaid cards; trusted CDN bot scores through forwarded-header trust;
    - a shadow queue for deterministic cases.
  - **Risk pipeline.** `ISparkRiskSignal` produces evidence, combined into a score: allow, log for review, challenge or honest slow-down (429 + `Retry-After`). Visitors are **blocked only on deterministic evidence**. The thresholds are starting values, calibrated in S12 until 0 humans are blocked.
  - **Deceptive measures** (owner's choice): decoys and honeypots, fake sold-out pages for flagged bots, poisoned tokens, tarpits.
    - Each is a separate switch, **off by default**.
    - By default each triggers **only on deterministic evidence**: forged or replayed tokens, honeypot hits.
    - Lowering the trigger to a risk score is an explicit host choice.
    - Startup warns and the docs state the legal exposure (EU UCPD; GDPR Art. 22; research §6–§7).
    - Every deceptive action is logged so it can be appealed.

### 4.13 Approvals (D9, D26)

**Two layers (research §9).**

- **Decision requests.** The engine:
  - `ApprovalSubject(Type, Id, Key?)`;
  - `IApprovalDeciders<T>`: a live gate, plus decider ids for notifications;
  - `IApprovalHandler<T>`: approve, reject, withdraw and expire, each run inside the decision's session;
  - `ISparkApprovals`: `Submit` (with a session overload), `Decide` (a reject reason is required), `Withdraw`, `GetPending`;
  - a library `ApprovalRequest` PO type, visible to the proposer, the deciders and `VisibleTo`, with a uniform 404/401 refusal;
  - integrations with notifications, mail (deduplicated), audit, `IExpiring`, live updates and a write guard.
- **Document proposals** are built on top as one kind:
  - The configuration lists the types that need approval for Create and/or Modify.
  - Saving produces a proposal: the full document plus the base change vector. This needs the `ISaveReplacement` seam (X7).
  - Approving applies the change as the approver with `Etag = base`; a 409 makes the proposal stale.
  - Pending creations are invisible to queries.
  - A generic attribute diff covers most types; seat layouts get a custom visual diff.
- **Rights.** No library rights are bound to an `approvals:approvers` slot, because an unbound slot refuses startup and would reach every app transitively (`SparkSecurityLayers.cs:290-298`, `LibraryLayersReader.cs:40`). Apps grant rights per type, in the Moderation style.
- **Migrated in this PR (D26):**
  - IdP developer request;
  - IdP cross-owner scope approval (a handler patches the row; never a proposal, since the app's change vector moves hourly);
  - IdP go-live review;
  - the **Organizations join request**, which gains in-app approval by an organization owner or admin, a decline option, and a notification to the requester. It ships with a production migration (§8).

  About 750–850 lines are removed net. **Not migrated:** MCP plans, Moderation, Contributions, invitations, OAuth/device/PAR/grants/PATs, and CodeCoverage's forge-native approvals (research §9).

### 4.14 Storefront and Angular entry points (D27)

- **Pages.** Libraries ship complete routed pages built from exported components: event list and detail with seat map, waiting room with countdown, basket, checkout, order and ticket pages, shop catalogue and product pages, and the scanner PWA. A host replaces any single page per route without forking.
- **Look and feel.** Everything is theme-driven (CSS variables; dark mode from #462), translatable, and meets WCAG. The buyer seat picker has an accessible list alternative (block + quantity = best available).
- **Server-side meta tags (X6).** The .NET host injects the title, description and OpenGraph image into `index.html` for public routes. This gives link previews and indexing without Node or SSR; no app in this repo uses Angular SSR (`docs/issue_462_PRD.md:28-29`). All seller text is escaped.
- **Discovery beyond meta tags (GB9 → X22).** A generated `sitemap.xml` (public events, dates, products; per language), schema.org **JSON-LD** (`Event` with offers, location, start in the venue's offset, and status; `Product` with offers, GTIN, availability) injected by the same middleware, and `hreflang` alternates for translated routes. SeoTester's checks validate them (S19 precedent). `.ics` files are in §4.11.
- **The hold timer and WCAG 2.2.1 (GA10, round 5).** A hold is a time limit. The basket shows the deadline as text plus a countdown announced through a polite live region at 5 min, 1 min and 20 s (not every second). **At least 20 s before expiry** the buyer gets a single-action **"More time"** button that extends the hold by the channel's `Hold:Extension` (default 5 min), repeatable until the seller's `Hold:Max` (D2) is reached. Beyond `Hold:Max`, the PRD relies on the **real-time / essential exception** of SC 2.2.1: the hold reserves scarce stock against other buyers in real time, and an unlimited extension would let one buyer block it. That reasoning is stated in the accessibility statement and must be confirmed against W3C's *Understanding SC 2.2.1* when implemented (D37). The `PaymentPending` window (D3) is the provider's limit, not ours, and is shown the same way.
- **Accessibility deliverables (GA10, EAA / Directive 2019/882).** Each storefront host gets an **accessibility statement** page (EAA Annex V information: how the service meets the requirements, contact and complaint route; text supplied by the host from a library template; microenterprises providing services are exempt, so it is a startup **warning**, not a refusal). **Automated axe checks** run in E2E on the storefront, basket, checkout, waiting room, seat picker (list alternative included), withdrawal function and consent banner; a violation of WCAG 2.1 AA rules fails the test.
- **Customer account area (GB10).** A routed page for signed-in buyers: order history, invoice and credit-note downloads, the address book (reference-data `Address`, §4.18), ticket re-download and ticket-page links, open withdrawals and claims with the withdrawal function (GA1), consent settings (GA9) and back-in-stock subscriptions (GB11), and **attach guest orders** (D4: verify the e-mail address, then the guest orders with that address move to the account).
- **Embeddable sales widget (GB8 → X23).** A web component (`<spark-commerce-widget event="…">` / `product="…"`), loaded by one script tag on the organiser's own site, plus an iframe variant. It lists offers and starts checkout. Because third-party cookies may be blocked, the visitor cookie (X1) is not relied upon inside the widget: the widget hands over to a **top-level checkout window** (popup or redirect back to the platform) as soon as a hold or a sign-in is needed. Waiting-room-gated sales always open top-level. The host lists allowed embedding origins (`frame-ancestors`).
- **Legal pages and consent (GA2, GA9)** are routed pages too: terms, privacy policy, withdrawal information and model form, the accessibility statement, the trader page per seller (GA5), and the consent banner and settings (§4.20).

### 4.15 Security, privacy, audit (defaults 7, 8, 10, 11)

- **GDPR.** Buyer and attendee data plugs into `ISparkPersonalDataContributor<>` and `ISparkAccountDeletionHandler<>`. ~~Orders and invoices are kept for a retention period set by the host, then anonymised.~~ *(Under-specified, round 5 inconsistency 5.)* **Retention is per document type, floored by law:** the host sets a period per type (orders, invoices and credit notes, payments and disputes, attendee answers, back-in-stock subscriptions, tracking attributions, DSA notices, DAC7 records, trader data), and Commerce refuses a period below the **legal floor** of that type for the configured country (§4.8: invoices and accounting records, Belgium 10 years per the research; DAC7 records 10 years and DSA trader data 6 months after the relationship ends, per the research; **all floors to be confirmed per country**, D37). Issued invoices and credit notes are **never altered** by anonymisation; when the floor ends they are deleted whole or anonymised only in the order view. Orders whose invoice is still within its floor keep the invoiced data.
- **Ticket holders (round 5).** Holder data follows the holder, not the order; transfer and resale erase the previous holder's data from the ticket (§4.11).
- **Controller and processor roles (GB14).** In `ConnectedSellers` mode each **seller is the controller** of its buyers' and attendees' data for its own sales; the **platform is a processor** for that data (and a controller for accounts, fraud prevention, DSA and DAC7 duties). The library ships a **DPA template** (a host-editable document, §4.20) that sellers accept at registration (version stored), seller-scoped exports of buyers and attendees (CSV, scoped by the `finance` or `seller-admin` role, every export audited), and the data-access rights that keep one seller from reading another's buyers (Organizations row policy). The platform host confirms the roles with its own counsel (D37).
- **Consent (GA9 → X21)** is described in §4.20.
- **Audit trail** on every money-moving change: orders, refunds, payouts, price changes.
- **Rate limits** are keyed per identity, not per IP.
- **MCP.** Commerce write actions are not exposed to MCP agents unless the host enables it.
- **No string-spliced RQL anywhere** (repo rule). Identifiers go through `RqlIdentifier`.

### 4.16 Demo app (D22)

- **One app with both systems:** a shop channel and a ticket channel side by side, running in `ConnectedSellers` mode with the test provider and test carrier. `Platform` mode is covered by integration tests. If one app proves impossible (S0), the fallback is two apps.
- **Ports:** https 5015, http 5016.
- **Recipe:** the new-app checklist (research §10): workspaces, slnx, `pull-request.yml` build and verify loops, `verify-ngsw-config`, `test-local.mjs`, `regenerate.yml`, `test.yml` matrix.

### 4.17 Dates and times (D33)

- **Every date and time a viewer sees is localized.** Formatting follows the viewer's locale, and the time zone is the viewer's. This holds for event start and doors times too. It is the existing Spark mechanism and needs no new work in grids and detail pages:
  - display goes through Angular's `DatePipe` with no zone argument, so values render in the browser's zone (`docs/guide-dates-and-sorting.md:172-207`);
  - edits take the viewer's zone (`:236-251`);
  - the server knows the zone from the `X-Spark-Timezone` header or cookie via `IRequestTimeZoneResolver` (`:291-331`).
- **Venue hint.** When the viewer's zone differs from the venue's, event pages add the venue-local time as a **secondary label** ("20:00 in Brussels").
- **Server-rendered output** (mails, ticket PDFs, invoices, scanner screens) is formatted in .NET with the **recipient's stored time zone and culture** (X14). These run in background jobs with no request, so no viewer zone can be resolved (`:336-344`). The zone and culture are captured from the request at checkout onto the order, and onto the account when there is one. Event times in mails and PDFs carry the same venue-local hint.
- **Three kinds of time, one facility (D34, X16):** (1) the viewer's zone, (2) the stored wall clock with its offset, (3) a third zone, such as the venue's. They are declared per model attribute and rendered by a shipped ng-spark **component** (`<spark-date-time>`, with a pipe underneath), and by a .NET twin for server output. Commerce event times declare `primary: viewer, hint: zoneFrom Venue.TimeZoneId`, so the venue carries an IANA zone id from reference data (§4.18).
- **Component API (D35).**
  - `<spark-date-time [value]="dto" />` renders in the viewer's zone and locale.
  - `<spark-date-time [value]="event.startsAt" [alsoIn]="venue.timeZone" />` adds a second rendering, e.g. **21:00** *(20:00 in London)*. It is hidden automatically when both zones give the same wall-clock time at that instant, so the comparison is by offset on that date, not by zone name.
  - `[mode]="'stored'"` shows the stored wall clock with its offset.
  - The second zone is labelled with the translated city from the IANA id.
  - The model rule `dateTimeDisplay` drives the same component in generic grids and detail pages.
- **Zone labels (D36).** A core `ISparkTimeZoneNames` interface (server + client) resolves the label through a fallback chain:
  1. the **reference-data provider**, when the host wires the reference-data client: the translated CLDR exemplar city ("in Brussel");
  2. otherwise, the name the **platform localizes**: browser `Intl` `timeZoneName: 'longGeneric'` ("Midden-Europese tijd"), and .NET ICU names on the server (S23 verifies they are localized);
  3. last, the city **derived from the IANA id**, in English.

  Results are cached per (zone, language); the client fetches them once per session. Rendering never waits: the fallback renders first and the city swaps in without a layout jump.
- **Countdowns** (sale start, hold deadline) are zone-independent. They use the server-time offset carried in live frames (X2).
- **Storage** stays `DateTimeOffset` end to end. Comparisons use `.Offset`/`EqualsExact`, never `==` (`docs/guide-dates-and-sorting.md:164`).

### 4.18 Addresses (K8)

Venues, seller profiles, shipping addresses and invoices all need structured addresses.
- **Source.** Commerce consumes an `Address` value plus a typeahead component from the **reference-data** client package, whose design follows in its own PRD.
- **Endpoint.** Configurable by the host (K7): the host points at its own reference-data instance, or runs with free-text entry. We run no public deployment (K8).
- **Test data (D39).** The reference-data app's basic seed set (some countries, regions, places and streets) gives commerce's E2E and the demo real addresses without a full import.

### 4.19 Sellers and marketplace obligations (D21; round 5: GA5, GA6, GB4, GB14)

These apply in `ConnectedSellers` mode, except notice-and-action, which every host needs. Rules come from secondary sources (gap analysis) and are confirmed against EUR-Lex when implemented (D37).

- **Trader traceability (GA5, DSA Art. 30).** Seller registration (D21) collects the trader's name, address, phone and e-mail, an identity document (stored as a blob with restricted rights, `finance`/platform admin only), the payment account, the trade-register entry, and a self-certification that its products comply with EU law. The platform makes a **best-effort verification** (registry lookup where an adapter exists, VIES for the VAT id; otherwise a manual check recorded through Approvals) before selling is enabled, beside the provider's KYC gate (D21). Data is kept for **6 months after the relationship ends** (§4.15 floor). Name, address, contact and register number are shown on a **trader page** linked from every listing.
  - **Art. 29 micro/small exemption.** Contested (trader size or platform size?). The feature ships complete and `Sellers:TraderTraceability` defaults to **on**; a host may switch it off only with `AcknowledgeDsaArt29Exemption=true` (startup validation, §4.2).
- **Trader status and ranking (GA5, CRD Art. 6a as amended by the Omnibus directive).** Each listing states whether the seller is a **trader** or not (consumer protection does not apply to a consumer-to-consumer sale; the listing says so). The storefront's search and lists publish their **main ranking parameters** on a page generated from the configured sort rules.
- **P2B terms (GA5, Reg. 2019/1150).** Sellers accept versioned platform terms (§4.20) that include the ranking parameters, the grounds for suspension, and the **complaint-handling** process: a seller complaint is an Approvals decision request with a reason and a deadline. Complaint handling may be switched off by a small platform (`AcknowledgeP2BSmallEnterprise`).
- **Notice and action (GA5, DSA Art. 16/17).** Anyone (signed in or a visitor) can report a listing, event or review through a notice form (reason, explanation, location, contact optional, good-faith statement). The report becomes a **Moderation** item; a decision produces a **statement of reasons** sent to the seller (restriction kind, facts, legal or contractual ground, redress options). Notices, decisions and statements are retained (§4.15 floor) and counted for a transparency report export.
- **DAC7 seller data (GA6).** Collected at registration with the trader data: TIN (and issuing country), VAT id, date of birth (individuals), primary address, bank account (IBAN) and its holder. ~~Missing data after two reminders blocks payouts (the directive's enforcement step; confirm, D37).~~ *(Superseded by D43, round 6:)* missing data after two reminders (and the minimum wait) **closes the seller's sales** (`CanSellAsync` refuses with `commerce.seller.dac7Blocked`) until the data is complete; payouts at the provider are not touched (confirm the directive's enforcement step, D37). The report itself is in §4.8.
- **Seller reports and the fee ledger (GB4).** Per seller: sales, refunds, disputes, fees and payouts per period, matching the accounting export (§4.8) and the reconciliation (§4.5).
- **Seller data access (GB14).** Controller/processor roles, the DPA template and seller exports: §4.15.

### 4.20 Legal documents and consent (round 5: GA2, GA9, GA10)

- **Versioned legal documents (GA2).** A `LegalDocument` type in Commerce: kind (terms of sale, privacy policy, withdrawal information + model withdrawal form, platform/P2B terms, DPA, accessibility statement, cookie policy), owner (the platform, or a seller in `ConnectedSellers` mode), language versions (`TranslatedString`, X17, plus a PDF rendering, Q-O9), a version number and an effective date. Publishing a new version never edits an old one. A sales channel cannot open a sale for a seller whose required documents are missing (refused at "open sale"; warned at startup, §4.2).
- **Acceptance on the order (GA2).** The order stores the ids and versions of the documents in force at checkout (§4.4); the confirmation mail attaches them (durable medium, CRD Art. 8(7)). Account-level acceptance (platform terms, DPA) is stored the same way.
- **Consent primitive (GA9 → X21, core).** Generic, not commerce-specific (K6):
  - **purposes** declared by packages and the host (`necessary`, `analytics`, `marketing-mail`, `third-party-challenge`, `third-party-media`, …), each with a translated description and the vendors behind it;
  - a **record** per visitor (X1) or account: purpose, granted or refused, policy version, timestamp, source (banner, settings page, checkout checkbox); merged on sign-in like the basket; withdrawable at any time, as easily as it was given;
  - `ISparkConsent.Has(purpose)` on the server and a client service in `@mintplayer/ng-spark/consent` with a banner and a settings page (both replaceable, WCAG-checked, equal-weight accept and refuse buttons);
  - packages declare `RequiresConsent(purpose)`; startup refuses a package that requires consent when the primitive is not wired (§4.2).
- **Marketing e-mail (GA9).** Opt-in, or the **soft opt-in** checkbox at checkout for similar products of the same seller (ePrivacy Art. 13(2), confirm, D37), stored as a consent record; **every marketing mail carries an unsubscribe link** (one click, no sign-in) and a `List-Unsubscribe` header. Service mails (order, tickets, withdrawal receipts, back-in-stock requested by the buyer) need no consent.
- **Analytics and pixels.** The library ships none; a host that adds them declares the purpose and loads them only after consent.

### 4.21 Round-5 items: index (D37)

Every gap-analysis item is in scope (D37); none went to §9. Legal items are checked against EUR-Lex when implemented; adapters and integrations use public-documentation fixtures plus test adapters.

| Id | Item | Section | Package | Milestone |
|---|---|---|---|---|
| GA1 | Withdrawal function: button, confirmation, durable receipt; unconditional; delivery cost refunded; withholding until return | §4.7, §4.3, §4.9 | Checkout, Shop | M9, M12, M17 |
| GA2 | Legal documents, acceptance on the order, "order with obligation to pay", durable confirmation | §4.20, §4.4 | Commerce, Checkout, Invoicing (PDF) | M4, M7, M10 |
| GA3 | Omnibus prior price, price history, `FinalPrice`, unit price | §4.6, §4.4, §4.10 | Commerce, Promotions, Shop | M4, M8, M12 |
| GA4 | Place of supply, reverse charge, VAT territories, OSS export | §4.8 | Invoicing | M10 |
| GA5 | Trader traceability, trader status, ranking, P2B, notice and action | §4.19 | Commerce (+ Moderation, Approvals) | M4 |
| GA6 | DAC7 data and report | §4.19, §4.8 | Commerce, Invoicing | M4, M10 |
| GA7 | GPSR product information; lot → shipment → buyer recall trace | §4.10; warehouse §4.5 | Shop; Warehouse | M12; WM7 |
| GA8 | Legal-guarantee claims and information; return-reason taxonomy | §4.7, §4.10; warehouse §4.3 | Checkout, Shop; Warehouse Inbound | M9, M12; WM4 |
| GA9 | Consent primitive, marketing mail opt-in and unsubscribe, challenge consent refusal | §4.20, §4.12 | core (X21), Challenges | M1, M16 |
| GA10 | Accessibility statement, axe in E2E, hold timer and presence check vs WCAG 2.2.1 | §4.14, §4.12 | storefront entry points | M17, M18 |
| GA11 | Geo-blocking: same conditions, no card-country refusal | §4.4, §4.5, §4.2 | Checkout, Payments, Commerce | M4, M7 |
| GB1 | Disputes and chargebacks (frozen tickets, evidence pack, seller debit, I4 "dispute lost") | §4.5, §4.4, §4.3, §4.11 | Payments, Checkout, Tickets | M6, M7, M15 |
| GB2 | Multi-channel basket (D40); ticket add-ons | §4.2 | Checkout, Events | M7, M13 |
| GB3 | Attendee questions; check-in lists, multi-day passes, re-entry, zones | §4.11 | Events, Tickets | M13, M15 |
| GB4 | Settlement reconciliation, accounting export, platform-fee ledger, seller reports | §4.5, §4.8, §4.19 | Payments, Invoicing | M6, M10 |
| GB5 | Service points and lockers, return labels, multi-parcel | §4.9 | Shipping (+ adapters) | M11 |
| GB6 | Customs: HS code, origin, CN22/CN23, DAP/DDP | §4.9, §4.10 | Shipping, Shop | M11, M12 |
| GB7 | Back-office orders: comps, guest lists, phone orders, re-send, seat change, upgrade | §4.4, §4.11 | Checkout, Tickets | M7, M15 |
| GB8 | Embeddable sales widget (X23) | §4.14 | storefront, core | M17 |
| GB9 | Sitemap, JSON-LD, hreflang (X22); `.ics` | §4.14, §4.11 | core, Tickets | M15, M17 |
| GB10 | Customer account area | §4.14 | storefront (Checkout, Shop, Tickets pages) | M17 |
| GB11 | Back-in-stock notifications | §4.6 | Promotions | M8 |
| GB12 | Age-restricted items: flag, attestation, door/handover ID check | §4.4, §4.10, §4.11 | Checkout, Shop, Tickets | M7, M12, M15 |
| GB13 | Product media and faceted filtering | §4.10 | Shop | M12 |
| GB14 | Controller/processor roles, DPA template, seller exports | §4.15, §4.19 | Commerce | M4 |
| GC1 | Abandoned-basket reminder (needs consent) | §4.4 | Checkout | M7 |
| GC2 | Affiliate and promoter tracking links | §4.6 | Promotions | M8 |
| GC3 | Donations and round-up | §4.4 | Checkout | M7 |
| GC4 | Ticket insurance adapter | §4.4 | Checkout (+ `.Test`) | M7 |
| GC5 | Product reviews with verification disclosure | §4.10 | Shop (+ Moderation) | M12 |
| GC6 | Google Merchant / Meta product feed | §4.10 | Shop | M12 |
| GC7 | Per-event ticket-PDF templates | §4.11 | Tickets | M15 |
| GC8 | IOSS | §4.8 | Invoicing | M10 |
| GC9 | Dangerous-goods flags and carrier-service restrictions | §4.9; warehouse §4.5 | Shipping; Warehouse | M11; WM3, WM7 |
| GC10 | EU VAT rate table (TEDB) as an optional reference-data source | §4.8; reference_data_PRD §4.4 | Reference data, Invoicing | M19, M10 |
| GC11 | Holiday calendars | §4.9; reference_data_PRD §4.4 | Reference data, Shipping | M19, M11 |

**Inconsistencies resolved in round 5** (gap analysis §"Inconsistencies"): 1 basket vs festival → D40 (§4.2); 2 stale status → header, §0, M0; 3 Q-O5 vs K8 → §11, D39; 4 superseded stress criteria → §7, D23, §14; 5 retention → §4.8, §4.15; 6 withdrawal as seller-approved → §4.7, §4.10, D20; 7 VAT omissions → §4.8; 8 `FinalPrice` → §4.4; 9 waiting list for shops → §4.6; 10 ticket-holder data → §4.11; 11 hold countdown → §4.14; 12 dispute missing from I4 → §4.3; 13 K10/K12 order → §1.

---

## 5. Framework additions (Spark core and existing libraries, same PR)

| # | Addition | Why | Evidence of the gap |
|---|---|---|---|
| X1 | **Visitor identity.** Signed HttpOnly cookie through Spark Data Protection; `ISparkVisitor`; visitor → account merge on sign-in (basket and queue entry); **antiforgery on anonymous POSTs** when a visitor cookie is present | Guests (D4) need a refresh-safe basket and queue entry; a visitor cookie would otherwise be ambient authority | No guest identity in libs; anonymous callers keyed by IP (`LiveSocket.cs:89-92`, `SparkQuotas.cs:28-31`); anonymous POSTs not CSRF-checked (`docs/guide-controllers.md:74-79`) |
| X2 | **Live broadcast topics** (one server value fanned out with no per-viewer re-read), **server time in frames**, a **private per-identity stream**, and socket caps keyed by identity | Queue at 200k (D23; that target is no longer measured, round 4), countdown, admission push | Every change re-reads per viewer (`docs/guide-live-updates.md:31-32`); `LiveQueryArgs` has no user (`LiveQueryArgs.cs:21-48`); 5 sockets per IP (`SparkLiveOptions.cs:48`); `ping` carries nothing (`:157`) |
| X3 | **Atomic natural-id create.** `StoreAsync(e, string.Empty, id)`, with takeover of expired `IExpiring` documents | Races on create; expired holds collide for up to 36 h | Probe then store (`DatabaseAccess.cs:250-276,827-828`) |
| X4 | **`Money` value type** (amount + currency), model attribute type, renderer, index companions | Prices, fees, refunds | Only `decimal` (`SparkModelShape.cs:203`) |
| X5 | **`MintPlayer.Spark.Webhooks`.** Signature strategies (HMAC header, Stripe scheme with tolerance, fetch-by-id for Mollie), raw-body preservation, dedupe via `BroadcastOnceAsync`; GitHub webhooks re-pointed onto it | Payment providers, carriers, e-invoicing callbacks | `SignatureService.cs:11-37` is internal and GitHub-specific (`sha256=`); route hand-mapped (`docs/endpoints_generator_webhooks_exception.md`) |
| X6 | **Server-side meta tags** for SPA routes | Link previews and SEO of public event and product pages | No SSR (`docs/issue_462_PRD.md:28-29`) |
| X7 | **Save-pipeline seams.** `ISaveReplacement` with a first-class "submitted for approval" result; an on-behalf-of seam; apply as approver with `Etag = base` | Document proposals (D9) | Interceptors cannot skip persistence (`SparkInterceptors.cs:22-24`) |
| X8 | **Organizations:** an opt-in "publishable" OR mode, and custom roles (`scanner`, `finance`) | Public sales and products owned by sellers; door staff | Row policy is false for anonymous callers and non-members (`OrganizationRowPolicy.cs:28-31,85-86`); row policies are AND-only (`docs/guide-row-security.md:81-106`) |
| X9 | **Persistent-object name identity (D28).** Optional `PersistentObjectName` attribute (generic and non-generic), analyzer SPARK057–062 with code fixes, explicit `"$extends"` deltas, actions resolved by contract, rename handling, stored type names → ids | Library types with natural names (`Order`, `Product`, `Ticket`) | Research §8: synchronizer hijack, wrong refusal advice, startup-only lib/lib check, silent orphaned delta, first-request actions crash |
| X10 | **Rate limiter.** Identity-keyed partitions + IPv6 /64 grouping | Fans behind CGNAT; address rotation inside a /64 | Exact-IP key (`SparkBuilderRateLimiterExtensions.cs:115`) |
| X11 | **`SparkUser.CreatedAtUtc`** | Minimum-account-age opt-in (D29) | No creation timestamp (research §6) |
| X12 | **Generic claims primitive** in core: reserve, convert and release with lazy expiry over compare-exchange, plus a cluster-wide multi-key unit of work | Seats, per-buyer limits, single-use admission tokens, one-entry-per-identity, approvals, ballot links | The pattern exists only in internal copies (`UserStore`, `MessagingLease`, `DashboardSamplerLease`, `OidcClientIdReservation`) |
| X13 | **Phone verification** in Authorization, on top of `MintPlayer.Spark.Sms` | D29 opt-in | No SMS flow in libs (research §6) |
| X16 | **Date-time display facility (D34).** Model attribute rule `dateTimeDisplay`: `viewer` (default) \| `stored` \| `{ zone: "<IANA>" }` \| `{ zoneFrom: "<attribute>" }`, plus an optional `hint` rendering. A shipped ng-spark **component** `<spark-date-time>` (with a pipe underneath) built on `Intl.DateTimeFormat`, used by generic grids, detail pages and custom pages. A fixed zone label (the translated city from the IANA id, not browser abbreviations). A .NET `ISparkDateTimeFormatter` with the same semantics for mails and PDFs, where "viewer" means the recipient's zone (X14). The identity provider's private date pipe is folded in. **Enforcement (K10):**
- server: a `BannedSymbols.txt` for every non-core project, through the existing `BannedApiAnalyzers` wiring (`Directory.Build.targets:235-241`). It bans `TimeZoneInfo.ConvertTime*` / `FindSystemTimeZoneById`, `DateTime.ToLocalTime`, `DateTime.Now` and `DateTimeOffset.Now` (use `TimeProvider`), with messages naming the built-in to use;
- client: a `tools/verify-*.test.mjs` guard (the repo's existing pattern) that rejects `Intl.DateTimeFormat`, `timeZone:` options and `DatePipe` zone arguments outside the core component. It ships with a violating fixture, so the guard is proven to fire (memory: *a clean grep is not evidence*) | The owner's three kinds of time; the D33 venue hint in generic screens; no private copies; K10 | `DatePipe` ignores IANA zone ids and silently falls back (`docs/guide-dates-and-sorting.md:336-339`); a duplicate `Intl.DateTimeFormat` pipe exists in `ng-spark/identity-provider/core/src/text.ts:46-54`; `parsedDate` (`ng-spark/pipes/src/parsed-date.pipe.ts:20`) only parses |
| X17 | **One name system: `TranslatedString` redesigned** (decided in the reference-data grilling, RD7). Today it is a flat `Dictionary<string, string>` + translation `Key`, `Create(en, fr, nl)`, and a fallback ending in "first value available" (`libs/model/MintPlayer.Spark.Model/TranslatedString.cs:7-51`). Entries carry a value + RFC 5646 tag, plus optional kind (official/common/exonym/historic), validity, source and romanization origin; a one-line API for app texts; an RFC 4647 fallback chain (server + client); any language; sanitized index field names; a shared display rule. Every existing value is migrated (no backward compatibility, K3) | Seller texts (D-DEF 6) and reference-data place names use one system; the owner rejected two | Today: one value per language, the helper covers en/fr/nl only, a `zh-Hant` key likely breaks generated index field names (reference-data research) |
| X18 | ~~**Lucene enabled automatically for fuzzy indexes** (reference-data grilling, RD8). A field or a model search rule marked fuzzy makes Spark's index registration set `SearchEngineType.Lucene` on that index; other indexes keep the server default (Corax on Community). An engine change triggers a deterministic side-by-side rebuild on deploy. An analyzer flags fuzzy queries against unmarked indexes~~ *(opt-in design, superseded by D38, round 5)*<br>**Fuzzy by default (D38).** Every index with **searchable fields** runs on **Lucene**, and Spark search is **typo-tolerant by default**: a search term that finds too few exact or prefix matches is retried fuzzily on the searchable fields. A field opts out per field (`fuzzy: false` in the model search rule, or the index-declaration marker, X20 item 4); an index whose searchable fields have all opted out, or that has none, ~~keeps the engine Spark pins for it (the server's default at the time of the change, pinned explicitly so a licence upgrade never switches it). Spark registers the engine **explicitly on every index**.~~ *(superseded by D38 as built and RD11, §13 "X18 as built")* keeps the **server default** engine: Spark sets the engine only on indexes with a searchable field or a fuzzy opt-in, and pins none. Existing search indexes change engine and **rebuild side by side** on the next deploy, production apps included (§8). The analyzer now flags fuzzy queries against opted-out fields | Typo-tolerant typeahead (reference data) and any app search, on every licence | Corax refuses `Fuzzy` at runtime (`InvalidQueryException: Method 'Fuzzy' is not supported`, memory measurement on 7.2.5); the engine is selectable per index on every licence ([docs](https://docs.ravendb.net/7.1/indexes/search-engine/corax)); an engine change applies only to new or reset indexes |
| X19 | **Library-declared indexes emitted into the app** (reference-data Q-R6, accepted for all three PRDs). Libraries declare index definitions, including maps contributed to a shared multi-map index; the app's generator emits the index classes at build time. This extends `[GenerateIndex]` to hand-written and composed indexes | Owner rule "the RavenDB Indexes ALWAYS live in the application" kept, with no copied code in hosts (K12); one shared multi-map index per library family keeps the index count small (the Community cap of 12 static indexes was lifted on 2024-05-22; see S18) | `GenerateIndexGenerator` already emits only in the application (OutputKind Console/Windows) and reads referenced assemblies (memory, #388/PR #484); a C# multi-map index can only map types it references |
| X20 | **Source-generator programme (K12, accepted 2026-10-17)**, applied across all three PRDs:<br>**1.** D28 names: the names generator emits effective names after renames plus each library's ids; unused constant classes are used or deleted.<br>**2.** **TypeScript contracts from C#:** a build-time tool extending `tools/SchemaGenerator` (`JsonSchemaExporter`) emits checked-in `.ts`, with a CI diff check. Scope: all new endpoints, plus migration of the existing hand-written TS mirrors. Prerequisite: typed records instead of anonymous objects for ng-spark-consumed responses. **Layout (investigated):**<br>• files `libs/node_packages/ng-spark/<entry>/src/generated/contracts.ts` (one per entry point, re-exported) and `apps/<App>/<App>/ClientApp/src/app/generated/contracts.ts`;<br>• namespaces mapped by `[assembly: SparkClientModule(...)]`; an unclaimed type is an error;<br>• committed, with a CI verify step and merge-queue regeneration;<br>• library contracts via `tools/SchemaGenerator contracts [--verify]`, app contracts via the app binary (`--spark-synchronize-contracts` / `--spark-verify-contracts`).<br>**No `MintPlayer.Dotnet.Tools` change needed:** a Roslyn generator can't write `.ts` files (measured). **Polyglot (checked at the owner's request): not for contracts.** Measured against v0.10.1:<br>• `long` → `bigint` while the wire sends numbers;<br>• unions serialize as `{}`, and `[JsonPolymorphic]` can't be added;<br>• TS records are classes and enums are numbers;<br>• global-namespace collisions between libraries;<br>• a second source of truth.<br>It is used only for the **GS1 core** (item 6).<br>**3.** One shared discovery generator, replacing 7 registration generators + `SparkFullGenerator`'s duplicate, and covering row policies, `IReplicaChanged<T>`, approval deciders/handlers and account hooks behind one generated entry point.<br>**4.** `GenerateIndexGenerator` extended: `Money`/`Quantity` companions, sanitized per-language field names (X17), the fuzzy marker (X18; since D38 an **opt-out** marker, fuzzy being the default), and the **record fix** (it only matches `class`).<br>**5.** An enum-backed lookup generator (G1, decided): `[SparkLookup] enum` → a generated lookup class with keys `lookup.{Enum}.{Member}` resolved through `translations.json` (the existing `Key` mechanism; library translation layers), with the translation-key guard failing on missing or unused keys. It replaces the hand-restated lookup classes and their inline `_TS(en,fr,nl)` labels.<br>**6.** GS1: the AI tables stay data (warehouse Q-W16). The **GS1 core** (element strings, Digital Link, linters, check digits) is written once in **MintPlayer.Polyglot** `.pg` and compiled to C# (internal, behind a hand-written facade) and TS. This applies **only if** a Polyglot release fixing [MintPlayer.Polyglot#73](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73) ships before warehouse WM2; otherwise both parsers are hand-written against GS1 Syntax Engine vectors (owner decision). The regeneration script uses the CLI binary that the `MintPlayer.Polyglot.MSBuild` NuGet restore already puts on disk. Polyglot types never cross the wire.<br>**7.** ETL scripts generated at runtime from replication contracts (complex projections refused at startup); replicas reuse the contract record.<br>**8.** Runtime plugin helpers instead of a generator: public `AddSparkConfigurationSection<T>`, a static `SectionName`, an idempotency helper, a one-per-slot provider helper.<br>**9.** Guards: a translation-key verify test (2 keys missing today: `dashboards.yes`, `common.unexpectedError`); K10 through BannedApiAnalyzers; migration `Version` derived from the class name; an endpoint filter replacing 33 identical `EnsureAuthorizedAsync` try/catch blocks. | Less code written by hand, compile-time completeness, no TS drift | Source-generator study: two TS drifts exist today (`PersistentObjectAttribute.options` missing; 6 of 11 always-sent `EntityPermissions` fields marked optional); `AddSubscriptionWorkers()` and the generated name constants are never called or used; `GenerateIndexGenerator`'s syntax filter matches only `class` (`:72`); `IndexNaming.cs:92` emits an invalid identifier for `zh-Hant`. Not done (not worth a generator): importer parsers, lineage tables, approval logic, typed ids, the 5 bespoke JSON converters, DTO mapping, constant classes, messaging lanes |
| X15 | **po-form edit renderer for a single AsDetail value object** (e.g. an `Address` on a venue, seller profile or order) | Addresses everywhere in commerce (§4.18) | A single AsDetail attribute can never get an edit renderer (`spark-po-form.component.html:381-398`) |
| X14 | **Recipient locale for server-rendered output.** Time zone and culture stored on the account (preference) and captured onto orders at checkout; an `ISparkRecipientLocale` resolver that mails, PDFs and background jobs use to format dates | D33: mails and PDFs must be localized, and background jobs have no request | The guide requires formatting such values in .NET with a resolved zone (`docs/guide-dates-and-sorting.md:336-344`); `IRequestTimeZoneResolver` needs a request (`:291-331`) |
| X21 | **Consent primitive** (round 5, GA9): purposes declared by packages and hosts, a consent record per visitor (X1) or account with policy version and source, merge on sign-in, withdrawal, `ISparkConsent` server-side, `@mintplayer/ng-spark/consent` banner + settings page (replaceable), `RequiresConsent(purpose)` declarations checked at startup (§4.20) | ePrivacy Art. 5(3) and GDPR consent for third-party challenges, analytics, marketing mail (research §7.3: CNIL fines) | The gap analysis found no consent component in `libs/` (keyword grep, 2026-10-19) |
| X22 | **Discovery output** (round 5, GB9): `sitemap.xml`, schema.org JSON-LD (`Event`, `Product`) and `hreflang` from the X6 middleware, declared per public route | Rich results and translated-page indexing; X6 covers only title, description and OpenGraph | §4.14; S19 showed the X6 middleware can rewrite `index.html` per route |
| X23 | **Embed mode** (round 5, GB8): a web-component bundle plus iframe variant; a top-level hand-over (popup or redirect) whenever a hold, a sign-in or a waiting room is needed, because the X1 visitor cookie is third-party inside an embed; `frame-ancestors` allow-list | Selling on the organiser's own site (pretix widget, Eventbrite embedded checkout) | X1 relies on a first-party cookie; third-party cookies may be blocked |

---

## 6. Defects found during the investigation (fixed in this PR, red test first)

| # | Defect | Evidence | Severity |
|---|---|---|---|
| F1 | An IdP developer team can **approve its own** cross-owner scope request. `Status` is writable, Developers hold Edit, and the interceptor resets only *added* scopes | `OidcApplicationScope.json:46-54`; IdP `security.json:51-55`; `OidcApplicationInterceptors.cs:171-180` | **Security** (suspected; confirm red) |
| F2 | Moderation decide race: the check and the status write run in separate sessions around side effects | `SparkModeration.cs:493-551` | Correctness |
| F3 | IdP decisions never write a reject reason; `Revoked` is never produced | research §9 | Correctness |
| F4 | MCP reports a cancelled update as "Applied" | `PersistentObjectSavePath.cs:165-170`; `Create.cs:89-92` | Correctness |
| F5 | The rate limiter keys on the exact IP; no IPv6 /64 grouping | `SparkBuilderRateLimiterExtensions.cs:115` | Abuse / fairness |
| F6 | Natural-id create is probe-then-store: two racing creates, last write wins | `DatabaseAccess.cs:250-276,827-828` | Correctness |
| F7 | Synchronizer hijacks a library type when an app adds a class with the same name, so library rights cover app documents while row policies don't | `ModelSynchronizer.cs:156,765`; `SparkLayers.cs:309`; `IRowPolicy.cs:150` | **Security** |
| F8 | A collision refusal advises "never state an id", which leads straight to F7 | `SparkModelFiles.cs:84-85` | Correctness |
| F9 | An orphaned delta is skipped with only a console line, so its restrictions are lost | `ModelLoader.cs:111-120` | **Security** |
| F10 | The duplicate-`{Entity}Actions` crash surfaces on the first request, because startup swallows it | `SparkMiddleware.cs:827-849` | Reliability |
| F11 | Same-name custom actions: the first loaded silently wins | `CustomActionResolver.cs:87-92` | Correctness |
| F12 | Replication write-back: a refusal (owner answers 207, including transient conflicts) is recorded as Completed, because the worker treats any 2xx as success | `SyncApply.cs:149-173`; `SyncActionSubscriptionWorker.cs:75` | Correctness |
| F13 | Replication dispatch isn't durable: it runs as `IAfterSave` with the sync record in a separate session, where `IAfterSaveCommitted` exists for exactly this | r14 | Correctness |
| F14 | Replication retries give up after about 5–8 minutes and Failed is final; the guide's "exponential backoff / retried until registered" is wrong | r14; `docs/guide-cross-module-sync.md` | Reliability / docs |
| F15 | Replication retries can let an older write overwrite a newer one for the same document | r14 | Correctness |
| F16 | The consumer's ETL script is deployed verbatim, so `load(id)` in it can read any document beyond the `Replicate/{Collection}` grant | r14 | **Security** |
| F17 | Attribute rights don't apply to module sync | `docs/guide-authorization.md:122` | **Security** |
| F19 | The ETL deploy endpoint never checks `TargetDatabase`/`TargetUrls`, so the calling module chooses where the owning module's server sends the replicated data | `docs/warehouse_picking_research.md` §4 (found during verification, not yet assessed) | **Security** (suspected; confirm red) |
| F18 | ETL tasks are never removed; nested write-back values are suspected to be sent as JSON text; the guide has drifted from the code; ETL deployment has no licence check (500 + dead-lettered message on Community) | r14; `EtlTaskManager.cs:92` | Reliability |

---

## 7. Spikes

Every spike ends with a measured result recorded in §13: numbers, file:line, or a red→green test. Environments follow D24:
- **Engine spikes** run on the owner's machine, in windows the owner chooses, against RavenDB pinned to **3 cores** (localhost:8080 has a Developer licence) with **no Community-missing feature used** (S18).
- ~~**The 200k queue spike** runs on temporary cloud VMs that the owner provisions and destroys.~~ *(Dropped in round 4: "No, I don't have any money for that".)*
- **Round 4/5:** no load or stress runs. Adapter spikes (S5, S6, S13, S14) are fixture-based, from the providers' public documentation, until the owner's development accounts exist; round 5 extends them to the new adapter features (disputes, settlements, service points, customs) under the same rule (D37).

| # | Spike | Question | Pass criterion |
|---|---|---|---|
| S0 | Demo-app feasibility | Can one app host both systems, with one App_Data, model sync and security sync? (D22) | `--spark-synchronize-model` and `--spark-verify-model` green with both channels; otherwise fall back to two apps |
| S1 | Engine correctness | Does any interleaving oversell? | ~~**0 oversell** over ≥100 randomized runs × 1,000 concurrent claimants~~ *(full size dropped, round 4)* **0 oversell at CI size** (3 runs × 100 claimants, passed, round 3) across multi-quota, seats and per-buyer keys; I1–I5 asserted; sweeper killed mid-run |
| S2 | Engine throughput | How many holds per second on one 3-core node? | ~~**≥150 successful holds/s sustained, p95 ≤250 ms** (D23); K ∈ {1, 8, 32, 64} shards; chain-cursor contention measured; otherwise record the ceiling and the lever (shards / admission rate / licence)~~ **Dropped (round 4).** The harness stays, env-gated; the levers (K, admission rate, one chain per block group) are configurable, and the target is not measured |
| S3 | Sweeper and live availability | How fast does freed stock show? | Expired holds show as available within ≤5 s; correctness unaffected with the sweeper off |
| S4 | ~~Waiting room at scale (cloud)~~ **Dropped (round 4)** | ~~Broadcast fan-out and join burst at 200k~~ | ~~Positions refreshed ≤5 s for 200k sockets; admission-worker failover with no double admission; memory per socket recorded~~. Admission-worker failover with no double admission stays covered by a CI-size test in M16 |
| S5 | Provider sandboxes → **documented-API fixtures** (round 4) | Real Mollie and Stripe behaviour | Per-method expiry, cancel, refund, late webhook, duplicate webhook, card fingerprint timing **recorded as fixtures**; the Stripe held-flow shape decided. **Round 5:** dispute/chargeback events (GB1), settlement and payout reports (GB4) and split capability (D40), from public docs |
| S6 | Connect onboarding | Mollie Connect and Stripe Connect | OAuth/onboarding, KYC-status updates, application fee, refund from the seller balance; insufficient-balance failure reproduced |
| S7 | Seat-map rendering | 50k seats in the browser | ≥50 fps pan and zoom on a mid-range laptop; a usable low-end Android; level of detail working |
| S8 | Ticket hashing cost | PBKDF2 parameters | Server verify ≤10 ms; WebCrypto verify ≤300 ms on low-end Android; gate peak of ~20 scans/s within 3 cores |
| S9 | Offline scanner | List size and sync | A 50k-ticket list downloads, is stored and wiped; double entry across two offline devices detected on sync |
| S10 | Approvals seams | `ISaveReplacement` with AsDetail, references, concurrency | A proposal of a document with nested rows round-trips; a stale base gives 409 |
| S11 | Name-collision analyzer | Can the analyzer read referenced layers and report at symbol locations? | SPARK057–062 fire on fixtures with working code fixes; no diagnostic on intentional deltas |
| S12 | Bot defence (research §6, items S1–S10 there) | ALTCHA, tokens, identity, shared networks, limits, a11y, red team, TLS fingerprint, calibration, fairness | ALTCHA p95 ≤2 s on a low-end Android; **0 admission-token double-spends**; 20 tabs + 3 devices = 1 entry; 0 genuine users refused behind one IPv4 or IPv6 /64; Stripe refuses an over-limit order before charging, Mollie refunds it within 60 s; NVDA and VoiceOver pass; red team finds no second entry, no hold without admission, no hold over the limit; calibration blocks 0 humans; all clients see their position within 2 s of the start |
| S13 | Carrier sandboxes → **documented-API fixtures** (round 4) | Sendcloud, bpost, PostNL | Label created, tracking webhook received; real fixtures recorded. **Round 5:** service-point search, return labels, multi-parcel (GB5), customs documents (GB6), dangerous-goods service restrictions (GC9), from public docs |
| S14 | Peppol sandboxes | Two access points | UBL validated against EN 16931 Schematron; send and receive acknowledged. **Round 5:** reverse-charge (category `AE`) and intra-EU/export invoices validate too (GA4); fixtures from public docs (round 4) |
| S15 | Mass refund at scale | 50k refunds through the test provider with rate limits | Completes; resumes after a crash with no double refund; credit notes and counters exact |
| S16 | Visitor identity | Merge and CSRF | Visitor → account merge keeps the basket and the best queue entry; anonymous POST without antiforgery refused |
| S17 | Live topics | Broadcast + private stream design on `/spark/live` | Topic delivered without a per-viewer read; private stream bound to identity without `HttpContext` in `LiveQueryArgs` |
| S18 | Community feature checklist | Is everything used available on the Community licence (server 7.1.10 in production, client 7.2.6)? | A written checklist: compare-exchange, cluster-wide transactions, subscriptions ≤3 per database, no reliance on the expiration sweep, **no ETL**, one custom analyzer per database, revisions limits, and the **index limits**. RavenDB **lifted** the Community caps (12 static / 24 auto per database) for newly issued licences on **2024-05-22**, and existing licences update when the server reaches `api.ravendb.net` ([discussion #18564](https://github.com/ravendb/ravendb/discussions/18564)); the buy page's footnotes still list the old caps ([pricing](https://ravendb.net/buy)). Read the production server's `/license/status` to confirm which applies. Only if a cap is still present, count the composed app's static indexes and consolidate |
| S19 | Meta tags in production hosting | Does injection work for the production static files and the dev proxy? | Link-preview card validated by SeoTester's OpenGraph checks |
| S20 | Fulfilment transport | The WD1 transport (SyncAction → owner → HTTPS push + digest reconcile; ETL only when licensed), on Community constraints; shared with the warehouse PRD's transport spikes | Stub consumer receives orders; ~~status write-back lands on the owner~~ status comes back as the warehouse's own contract (WD2; warehouse §4.3); replay and app-down scenarios lose nothing and duplicate nothing |
| S21 | Price/VAT rounding | Pro-rata discounts and partial refunds | Totals, VAT bases and credit notes reconcile to the cent across 10k generated orders |
| S23 | Zone-name fallbacks | Are .NET ICU zone display names localized by UI culture on Linux, and what does browser `Intl` `longGeneric` give per language? | A table for nl/fr/de/en: .NET (Linux container) + Chromium/Firefox/Safari outputs recorded; fallback step 2 kept only where it is localized |
| S24 | `TranslatedString` usage inventory (X17) | Where is `TranslatedString` used: model files, indexes, client renderers, stored documents, in every lib and app (production included)? | A complete inventory with file:line and the production collections affected; migration written red→green on real shapes |
| S22 | Production migrations dry run | D26 join requests and D28 names → ids (+ X17 if production documents hold `TranslatedString` values) | Migrations on copies of real production document shapes: red→green; idempotent re-run |

---

## 8. Production impact (VPS) (K9)

| Change | App | When | Safeguard |
|---|---|---|---|
| Organizations join-request migration (D26) | SeoTester (prod), others inert | Deploy of this PR | S22 dry run; old e-mailed links redirect; post-deploy check |
| Stored type names → ids (D28): moderation targets, agent grants, dashboard ids, share-link keys | CodeCoverage, SeoTester, SparkId (+ local demos) | Deploy of this PR | S22 dry run; idempotent migration; post-deploy check |
| Regenerated `modelHashes.json` / `securityPosture.txt` (D26, D28, new library layers) | SparkId, CodeCoverage, SeoTester, Fleet, QnA, HR, DemoApp | This PR | `--spark-verify-model` gates in CI |
| ~~New reference-data app deployment (K8)~~ | — | **not deployed** (K8 revised; D39 keeps it local with a basic seed set) | Runs locally and under test; hosts may deploy their own |
| Commerce demo deployment | demo | **not deployed** (WD11) | Runs locally and in CI E2E |
| **Fuzzy by default (D38):** every production index with searchable fields changes engine to Lucene and rebuilds side by side | CodeCoverage, SeoTester, SparkId (+ local demos) | Next deploy of each app after this PR | Side-by-side rebuild keeps the old index serving until the new one is non-stale; disk use roughly doubles per rebuilt index during the swap (reference-data §12), so check free disk on the VPS first; post-deploy check that searches return results and no index is errored |
| Resources (RAM, cores, Community licence) | VPS | ~~after S2/S4~~ (S2/S4 dropped, round 4) | ~~Spike numbers decide~~; no measured ceiling exists (round 4); a licence or VPS upgrade only if production use demands it. Today: Hetzner CPX32, shared vCPUs, about 3 GB RAM free (`docs/coverage_commit_graph_PRD.md:34`); Community licence expires 2027-03-31 |
| ⚠️ **Periodic backups** of the production RavenDB | VPS | owner's call, ideally before any migration in this PR | Production reports `HasPeriodicBackup: false` and has no external monitoring (reference-data research). The D26/D28 migrations change production data, so a backup must exist first |

---

## 9. Out of scope (genuinely not being done)

- **Apple and Google Wallet passes.** They need paid developer certificates per platform (D11).
- **Resale above face value.** Illegal in Belgium (Act of 30 July 2013); the exchange caps at face value (D30).
- **Stored-value gift cards.** Not requested, and stored value raises e-money regulation.
- **Season tickets, subscriptions, dynamic pricing, loyalty, backorders.** Not requested; classed as not needed (research §5).
- **Physical stock locations and warehouse operations inside commerce.** The warehouse plugin owns them (D13).
- **Angular SSR.** Replaced by server-side meta tags (X6, D27).
- **Vendors beyond the listed adapters.** Hosts add adapters through the public interfaces (K7).

*Round 5 (D37):* nothing from the gap analysis (GA1–GA11, GB1–GB14, GC1–GC11) is placed here; all of it is in scope (§4.21). POS (Q-O4) and B2B pay-on-invoice (Q-O8) remain open decisions, not exclusions.

---

## 10. Milestones (one PR)

Tests run once at the end, batched (repo rule). Each milestone is committed.

| # | Milestone | Depends on |
|---|---|---|
| M0 | Spikes ~~S0–S22~~ S0–S24 (results logged in §13; S2, S4 and the full S1 dropped in round 4) | — |
| M1 | Framework additions X1–X8, X10–X13, and defects F1–F6, F10, F11. Round 5: X18 revised to fuzzy by default (D38), X21 consent primitive (GA9) | S16, S17, S18 |
| M2 | Persistent-object name identity (X9, D28): attribute, analyzer, `$extends`, actions by contract, rename handling, F7–F9; stored names → ids migration | S11, S22 |
| M3 | Approvals library + the four migrations (D9, D26) | M1 (X7), S10, S22 |
| M4 | Commerce core: channels, bounds, sellers, fees, startup validation. Round 5: `FinalPrice` + price history (GA3), legal documents (GA2), trader traceability, ranking, P2B, notice and action (GA5), DAC7 data (GA6), seller data roles and exports (GB14), geo-blocking validation (GA11) | M1, M3 |
| M5 | Inventory engine (I1–I5, chain fill, per-buyer limits, sweeper, live topics) | S1 (CI size), S3, M4 |
| M6 | Webhooks (X5), Payments + Mollie / Stripe / Test, merchant modes. Round 5: disputes (GB1), settlement reconciliation + fee ledger (GB4), split capability (D40) | S5, S6, M4 |
| M7 | Checkout: baskets, orders, policies, extension, late payment, linked limits. Round 5: multi-channel basket (GB2/D40), terms acceptance + order button (GA2), Disputed state (GB1), back-office orders (GB7), age attestation (GB12), geo-blocking (GA11), abandoned basket (GC1), donations (GC3), insurance (GC4) | M5, M6 |
| M8 | Promotions + waiting list. Round 5: sale prices + Omnibus prior price (GA3), back-in-stock (GB11), tracking links (GC2) | M7 |
| M9 | Refunds and cancellations, mass refund. Round 5: unconditional withdrawal function (GA1), guarantee claims + return reasons (GA8) | M7, S15 |
| M10 | Invoicing + Peppol adapters + Test. Round 5: place of supply, reverse charge, territories, OSS (GA4), DAC7 report (GA6), accounting export (GB4), IOSS (GC8), TEDB suggestions (GC10), retention floors | M7, S14, S21 |
| M11 | Shipping + carriers + Fulfilment contract. Round 5: service points, return labels, multi-parcel (GB5), customs (GB6), dangerous goods (GC9), holiday calendars (GC11) | M7, S13, S20 |
| M12 | Shop. Round 5: returns per reason (GA1, GA8), unit price (GA3), GPSR (GA7), customs fields (GB6), media + facets (GB13), age flag (GB12), reviews (GC5), product feeds (GC6) | M11 |
| M13 | Ticketing events, dates, phases. Round 5: add-on ticket types (GB2), attendee questions (GB3) | M7 |
| M14 | Venues, layouts, editor, snapshots, approvals wiring | M3, M5, S7 |
| M15 | Tickets, scanner PWA, named / transfer / resale. Round 5: check-in lists, multi-day, re-entry, zones (GB3), comps / re-send / seat change (GB7), dispute freeze (GB1), ID check (GB12), holder-data erasure on transfer, PDF templates (GC7), `.ics` (GB9) | M13, S8, S9 |
| M16 | Waiting room, challenges, risk pipeline, bot defence, deceptive measures (guarded). Round 5: consent refusal for third-party challenges (GA9), presence check per WCAG 2.2.1 (GA10) | M1 (X1, X2, X12), ~~S4~~, S12 |
| M17 | Storefront pages + server-side meta tags. Round 5: discovery (GB9/X22), embed widget (GB8/X23), account area (GB10), hold timer + accessibility statement + axe (GA10), legal and consent pages (GA2, GA9) | M12–M16, S19 |
| M18 | Demo app (both systems) + E2E. Round 5: festival basket in one payment, axe checks | S0, M17 |
| M19 | Reference-data app (per `reference_data_PRD.md`), developed on this branch with a basic seed set (D39), + commerce address integration; TEDB rates (GC10) and holiday calendars (GC11) as optional sources | its PRD |
| M20 | Warehouse plugin (per `warehouse_picking_PRD.md`), including the cross-app transport and the replication defects F12–F19. Round 5: recall trace lot → shipment → buyer (GA7), return reasons in `expected-returns` (GA8), dangerous-goods flags (GC9) | its PRD, M11 |
| M21 | Guides and READMEs for every package | all |
| M22 | Test sweep, browser verification, version bumps, VPS checklist (§8) | all |

---

## 11. Open decisions

- **Q-O1 SMS adapters.** Which providers (K7)? *Recommendation:* Twilio and one EU provider, plus a test adapter, confirmed by a short research item.
- **Q-O2 Peppol access points.** Which two adapters? *Recommendation:* decide from a short research item comparing APIs, pricing and sandboxes (feeds S14).
- **Q-O3 Admission defaults.** Maximum active shoppers and the per-minute rate. *Recommendation:* ~~set from S2 and S4 measurements~~ (S2 and S4 dropped, round 4): conservative documented defaults, configurable per room, with the host tuning them from its own production metrics.
- **Q-O4 Box office / door sales (POS).** Selling at the door through the scanner app? *Recommendation:* ask in round 2; it reuses Checkout with a "cash/card-present" payment method.
- ~~**Q-O5 Deploy the commerce demo publicly?**~~ **Closed by WD11** (warehouse grilling, 2026-10-17): demos stay local; ~~only the reference-data app goes live. Revisit once the spikes measure VPS headroom.~~ *(Superseded: K8 revised the same day, so nothing goes live; and the S2/S4 measurements were dropped in round 4.)* **Round 5 (D39):** the reference-data app is developed on this branch and seeded with a basic set for testing; like the demos it runs locally and under test only.
- **Q-O9 PDF rendering.** Tickets, invoices, labels and packing slips need PDFs, and `libs/` has none; SeoTester prints through headless Chromium (warehouse research). Options: a .NET PDF library (licence check needed: QuestPDF's community licence has revenue limits; PdfSharp/MigraDoc is MIT), or headless Chromium (heavy in containers). *Recommendation:* decide by a spike shared with the warehouse PRD (WX6); prefer an MIT-licensed .NET library.
- ~~**Q-O6 Reference-data app.**~~ **Closed:** researched, grilled and written as `reference_data_PRD.md` (RD1–RD8 + Round 2, 2026-10-17).
- ~~**Q-O7 Warehouse plugin.**~~ **Closed:** researched, grilled and written as `warehouse_picking_PRD.md` (WD1–WD12 + Round 2, 2026-10-17).
- **Q-O8 B2B payment on invoice** (bank transfer, days to pay) in OnPayment channels? *Recommendation:* allowed only in OnPayment channels; stock is taken when the transfer is confirmed, and a race loser is refunded.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| One Community node can't reach 150 holds/s | ~~S2 measures;~~ (not measured, round 4) the levers are shards, the admission rate, or an in-memory allocator under a lease with batched commits. The ceiling is unknown and documented as such |
| The chain-fill cursor becomes the hot key | One chain per block group; holes-first allocation; ~~S2~~ (not measured, round 4) |
| 200k sockets exceed one box | Broadcast topics (X2); a polling fallback against a CDN-cacheable counter; ~~S4~~ (dropped, round 4: unmeasured) |
| **Round 5:** a legal rule is encoded wrongly because the gap analysis cites secondary sources (law-firm and vendor blogs, Wikipedia) | Each GA rule is checked against the official EUR-Lex text (and the national transposition where a member-state option applies) when its milestone implements it, with the article cited in code comments and the guide (D37); defaults are never presented as legal advice (§4.8 liability) |
| **Round 5:** adapter features (disputes, settlements, service points, customs, insurance, DAC7 XML) cannot be tested against the real third parties | Fixtures from public documentation, marked as such, plus `.Test` adapters (D37, round 4); replaced by recorded sandbox fixtures when the owner's accounts exist |
| **Round 5:** fuzzy by default (D38) rebuilds production search indexes and changes search results | Side-by-side rebuild; disk checked before deploy (§8); exact and prefix matches still rank above fuzzy ones; per-field opt-out |
| **Round 5:** scope growth (36 G-items) lengthens the PR | Accepted (K4, K5, D37); milestones committed separately |
| Late webhooks or provider outages strand holds | Lazy expiry (I3); re-claim the same units, else refund; idempotent confirmation |
| Connected-seller refunds fail for lack of balance | A refund-failed state, retry, notifications, a manual fallback (D20); S6 reproduces it |
| Tax rules wrong for some country | Configurable rules; explicit host responsibility; S21 reconciliation |
| Deceptive measures hit a human | Off by default; deterministic triggers by default; logged for appeal (D29) |
| Production migrations (D26, D28) strand data | S22 dry runs on real shapes; idempotent; post-deploy checks |
| Library name collisions | X9 (D28): analyzer, `$extends`, actions by contract |
| PR size and duration | Accepted (K5); milestones committed separately; tests batched at the end |
| Windows MAX_PATH in worktrees | Short folder names (D31) |
| ~~Community allows only 12 static indexes per database~~. **Corrected:** the cap was lifted on 2024-05-22 (#18564); the stale buy-page footnote misled this PRD. Residual risk: an old licence that never refreshed | S18 reads `/license/status` on production; multi-map indexes stay good practice (X19); the licence-capability check reports any cap that still applies |
| No ETL on Community breaks the planned cross-app sync | The warehouse PRD picks a Community-compatible transport (§4.9); S18 and S20 |
| No production backups while this PR migrates production data | Periodic backup configured and a restore tested before deploy (§8) |

---

## 13. Decision log

Owner answers are quoted where they changed or refined the recommendation.

### Round 1 (owner, 2026-10-17)

- **D1 (Q1):** One engine, three allocation modes chosen by configuration: `OnPayment` (claim at payment confirmation; race losers voided or refunded automatically), `HoldFromCheckout` (claim at "Pay", held while at the provider), `HoldFromSelection` (claim when the quantity is entered). Webshop default: `OnPayment`. *Evidence:* research §3, §5 (Vendure's pluggable allocation strategy, Saleor's reservations).
- **D2 (Q2):** Named channels in configuration, each with its own mode and settings, **plus per-sale values chosen by the seller within configured bounds**. ~~A basket never mixes channels.~~ *(Superseded by D40, round 5: an opt-in multi-channel basket.)* *Evidence:* owner chose C over plain channels.
- **D3 (Q3):** Paying pauses the clock. On redirect the hold becomes PaymentPending and is extended to the method's provider expiry plus grace, capped by `MaxPaymentWindow`. Slower methods are not offered in that channel. A late payment re-claims the same units, else is refunded. The provider payment is cancelled actively when the buyer cancels. *Evidence:* research §4 (Mollie per-method expiry; Stripe Checkout ≥30 min).
- **D4 (Q4):** `RequireSignIn` per channel. Guests get a new core visitor identity (X1). Guest orders can be attached to an account later by verifying the e-mail address. *Evidence:* research §1 gap 6.
- **D5 (Q5):** `Merchant = Platform | ConnectedSellers`. In connected mode the platform never holds third-party funds. *Evidence:* research §5, §7 (EU licensing of collecting for others).
- **D6 (Q6):** Mollie + Stripe + an in-process test provider, each its own package; real fixtures; zero-total orders skip the provider.
- **D7 (Q7):** Best available **and** pick your seat, per sale. Block kinds: Standing, SeatedUnnumbered, SeatedNumbered. Both are per-seat claims. Refined by D25 (chain fill).
- **D8 (Q8):** Shared venues, layouts owned by their creator; **a platform administrator must approve modifications, and (configurable) creations**. Owner: "this repo is only a framework, not a platform in its own right" (K6). Snapshots per published date.
- **D9 (Q9):** A generic Approvals library. Owner: "you may want to check if existing code can be trimmed and pointed to this approval library (like the moderations package)". *Evidence:* research §9.
- **D10 (Q10, decided after the bot study):** Three queue modes per sale within channel bounds: RandomizedPreQueue (default), Ballot, Fifo. Shuffled and ballot modes are refused in guest channels unless overridden. Owner, before the study: "Bots on ticket-sales websites are annoying… Do you think we can somehow avert bots, or toy with bots, while giving genuine users fair chances? I'd rather avoid relying on captcha's, but since this is only a framework, the package should provide such option anyways, through a simple configuration and maybe a c# extension method". *Evidence:* research §6.
- **D11 (Q11):** Ticket issuance + offline-capable scanning. **Plus the seller configures how far in advance tickets are delivered.** No wallet passes.
- **D12 (Q12):** A usable small shop **with carrier integrations**. Owner: "in the near future (or now) I also want to ship a reusable, generic warehouse-order-picking system plugin… 2 different web-applications for sure… ETL jobs are then used to consult information from the other app." → fulfilment contract (§4.9).
- **D13 (Q13):** The warehouse plugin goes in the **same PR, under a separate PRD**. The contract is designed against the real consumer and kept generic, with a stub consumer test.
- **D14 (Q14):** Sendcloud **and** bpost + PostNL + a test carrier. Owner: "Not up to us to decide here. The platform hoster should be able to freely decide this." (K7)
- **D15 (Q15):** General quotas (pretix model): an item counts against one or more quotas and sells only if all have room. Hot quotas are sharded. *Evidence:* research §5.
- **D16 (Q16):** A per-buyer limit across all orders and live holds; scope is the sale or the whole event; enforced as a quota; every return path returns it exactly once.
- **D17 (Q17):** Event → Dates → Sale phases; presale access codes per phase.
- **D18 (Q18):** Promotions (codes with effects, capacity-blocking codes) + waiting list (personal time-limited offers after a sell-out).
- **D19 (Q19):** Invoicing with gap-free numbering, credit notes, host-defined VAT, VIES, Peppol adapters; currency per seller. *Evidence:* research §7 (Belgian B2B e-invoicing since 2026-01-01).
- **D20 (Q20):** Seller refund policies within bounds, plus mass refund, reschedule window and 14-day withdrawal for goods; refund-failure states. *(Round 5 clarification: the seller refund policy never applies to the withdrawal, which is **unconditional**: no seller approval, delivery cost refunded, refund withheld at most until the goods are back or dispatch is proven (GA1, §4.7). The old §4.10 text "a buyer refund request the seller approves" contradicted the law and is struck.)*
- **D21 (Q21):** `Sellers:Registration = Closed | ApprovalRequired | Open`. ApprovalRequired uses Approvals; connected sellers are gated on provider KYC; Open + Platform is refused unless acknowledged.
- **D22 (Q22):** Owner: "If possible, I think one demo app, with both systems hooked up - Or else option B" (two apps).
- **D23 (Q23):** Stadium scale on one Community node: 50k numbered + 10k standing sold out ≤15 min; 200k queued; ≥150 holds/s, p95 ≤250 ms; zero oversell at 1,000 concurrent; positions refreshed ≤5 s. *(Superseded in part by round 4: these remain design targets, but none is measured. The 200k queue, the ≥150 holds/s throughput and the 1,000-concurrent run were dropped with S2, S4 and the full S1; zero oversell is guarded at CI size.)*
- **D24 (Q24):** Engine spikes on the owner's machine, ~~queue spike on temporary cloud VMs~~ *(dropped, round 4)*. Owner: "localhost:8080 has a developer license" → pin to 3 cores; Community-only features (S18).
- **D25 (Q25):** PreSelectOrder auto-holds at admission, with ranked alternatives and a presence check. Owner: "for small venues, the expidator may want to let the people choose their seats freely, while for stadiums the expidator may want to fill blocks in chains, which makes groups sit together instead of spread out" → chain fill (§4.3).
- **D26 (Q26):** Migrate all four decision workflows, including the production Organizations join request; fix F1–F4 red→green.
- **D27 (Q27):** Libraries ship complete, replaceable pages; server-side meta tags (X6).
- **D28 (Q28):** All five pieces of persistent-object name identity (X9). Owner: "Check if the Spark library can ship an assembly-level attribute that lets application developers specify the name of the json file… not sure how we should act when the developer decides to change this string though… PersistentObjects don't necessarily have to relate to a c# entity", then: "the PersistentObjectNameAttribute isn't necessary, If it's not present, the entity name should be picked. We should probably ship an analyzer that detects colliding PersistentObject names somehow". *Evidence:* research §8.
- **D29 (Q29):** The anti-bot design **plus deceptive measures** (owner's choice), guarded as described in §4.12. *Evidence:* research §6–§7.
- **D30 (Q30):** Named tickets, transfer and face-value resale, opt-in per sale; freeze before doors.
- **D31 (Q31):** Commerce packages grouped under `MintPlayer.Spark.Commerce.*`, generic ones flat; short folders.
- **D32 (Q32):** The full layout editor with CSV import and export.
- **Defaults (accepted):** 1 seller dashboards (Dashboards lib); 2 buyer mails (MailManager) + optional Web Push; 3 accessible seating; 4 price transparency with fees per channel (round 5: made precise as `FinalPrice`, §4.4); ~~5 venue time zone~~ (superseded by D33); 6 translatable UI and seller texts; 7 GDPR hooks + retention; 8 money audit; 9 hashed ticket secrets — owner: "use a strong hashing algo like bcrypt or something like that" → PBKDF2-HMAC-SHA256 (native in .NET and WebCrypto; tuned in S8); 10 identity-keyed rate limits; 11 no MCP buying by default.
- **K8 (owner):** the reference-data app, quoted in full in `reference_data_PRD.md` ~~once written~~ (written, 2026-10-17). Revised: not deployed (K8 row); round 5: developed on this branch with a basic seed set (D39).
- **K9 (owner):** "A redesign would be allowed too, but we'll probably need to change things on the vps then" → §8.

### Round 2 (owner, 2026-10-17)

- **D33 (Q33):** Owner: "make sure the date/time's presented to the viewer are always localized - timestamps are mostly stored using DateTimeOffset - I think we already do have the mechanism setup correctly". Every displayed date and time follows the viewer's locale and the **viewer's zone, event times included**. Event pages add the venue-local time as a secondary label when the zones differ (owner chose this over showing event times in the venue zone). Mails and PDFs use the recipient's stored zone and culture (X14). **Supersedes default 5.** *Evidence:* the mechanism exists (`docs/guide-dates-and-sorting.md:172-207,236-251,291-331`); server-rendered output needs a resolved zone (`:336-344`).
- **D34 (Q34):** Owner: "perhaps some of this logic can also be moved to a library (eg ng-spark) - But we have (1) time in viewer's timezone (2) stored time (3) time in a third timezone", then "by shipping a component of some kind". → A model-driven facility on client and server (X16): the attribute rule `dateTimeDisplay` (viewer | stored | zone | zoneFrom, plus a hint), a shipped `<spark-date-time>` component (pipe underneath, `Intl.DateTimeFormat`), and a .NET `ISparkDateTimeFormatter` twin. The identity provider's private pipe is folded in. *Evidence:* `DatePipe` can't take IANA zones (`docs/guide-dates-and-sorting.md:336-339`); duplicate pipe at `ng-spark/identity-provider/core/src/text.ts:46-54`.
- **D35 (Q35):** Owner sketched `<spark-date-time [offset]="datetimeoffset">` (viewer's zone) and `… [venueTimeZone]="venueLocation.timezone">` ("should render both the viewer's timezone and the venue timezone time"). Semantics confirmed; generic names chosen for the core component: `[value]`, `[alsoIn]`, `[mode]`. The second rendering is auto-hidden when the wall clocks match at that instant.
- **D36 (Q36):** Owner: "Do you think we could/should also add an optional service somehow that resolves the timezone code to the translated city-name through the newly implemented reference-data application, if wired up from the spark-application?" → Yes: a core `ISparkTimeZoneNames` with a fallback chain (reference-data provider when wired → platform-localized generic name → IANA-derived English city); the cache and non-blocking render are as described in §4.17. The reference-data app ingests CLDR time-zone exemplar cities (Unicode licence, attribution). *Evidence:* CLDR holds exemplar cities per locale; neither browsers nor .NET expose them; `Intl` exposes generic zone names only.
- **K12:** Owner: "we should also check if there are code features we could/should simplify using a source-generator in this repo. Sometimes source-generators can significantly reduce the amount of code we write". → A constraint (§1), backed by a dedicated study of the existing generators and boilerplate and of the three PRDs. The study's recommendations 1–9 were **accepted** as X20; its item 10 is not done. Owner: "Accept all, but for the typescript code, you'll need to check if the files should end up in a different output path. The base source-generator code can be found here: C:\Repos\MintPlayer.Dotnet.Tools\SourceGenerators … If code needs to be changed there, you'll need to create a detailed issue on that repository, and I'll hand it over". K12's earlier wording "code fixes in separate assemblies" was my error, corrected to the owner's 2026-10-05 rule.
- **TS contracts and Polyglot:** the owner accepted the contract layout and asked: "perhaps you could check if we should use C:\Repos\MintPlayer.Polyglot to compile pg scripts to typescript and c#". Measured: not for contracts; yes for the GS1 core, conditional on [Polyglot#73](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73) (filed at the owner's request), with hand-written twins as the fallback. Feature requests that would let more shared logic move to `.pg` are filed as [Polyglot#74](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/74) at the owner's request:<br>• fixed-point money with rounding modes and allocation, so basket previews equal server totals;<br>• consumer-exported cross-target `test` blocks;<br>• compile-time data embedding;<br>• a portable Unicode folding module;<br>• a portable pattern matcher.<br>Until they ship, money stays C# `decimal` and folding and patterns stay platform-specific with fixtures. The `MintPlayer.Dotnet.Tools` packing observation was not filed. Owner: "No, the user just needs to install the source-generator package and attributes package" (by design).
- **G1:** lookup labels go through translation keys with an enum-backed generator (`[SparkLookup]`, keys `lookup.{Enum}.{Member}` in `translations.json`), guarded by the translation-key test. The owner chose A, following the rule that App_Data refers to `translations.json` by key (`TranslatedString.cs:12-16`).
- **K11:** Owner: "Anything that can be done through the W3 browser-api's should probably be done so". → A constraint (§1): standard APIs first; standard-shaped polyfills where support is partial; a support matrix per feature.
- **K10:** Owner: "Now we only really need to bother with timezones in one place - all the complex timezone logic can be offloaded to core-framework shipped built-ins". → A design rule enforced by banned symbols (server) and a falsifiable verify test (client), in X16. *Evidence:* `BannedApiAnalyzers` already wired for test projects (`Directory.Build.targets:235-241`, `BannedSymbols.Tests.txt`).

### Round 3: spike results (M0, 2026-10-19)

The owner: "start with all spikes, then implement all milestones", with no full builds or tests in between. Spikes that need a later component or the owner's resources run when that milestone starts (plan rule: before the milestone that depends on them).

| Spike | Result | Evidence |
|---|---|---|
| **S18** Community checklist | **Pass, with constraints.** Compare-exchange, cluster-wide transactions, Changes API and bulk insert are all available. Subscriptions: 3 per DB; Spark core uses ≤2 (messaging + SyncAction), so a shop or warehouse app has 1 left (2 after WX3). The designs add none. No ETL; no periodic backup. **Engine constraints:** (1) cluster-wide sessions refuse optimistic concurrency and change vectors (client 7.2.6 strings), and conflicts surface as `ClusterTransactionConcurrencyException`; (2) only PUT/DELETE of documents and compare-exchange: no patch, counters, attachments or time series; (3) only engine sessions write engine documents, so engine collections are read-only in the UI; (4) no correctness from `@expires`/`@refresh` (≥36 h on Community); (5) revisions stay off on engine collections. Production `/license/status` (index caps, `HasTimeSeriesRollupsAndRetention`) is **for the owner to run**: the command is in the spike notes | Desk spike; [discussion #18564](https://github.com/ravendb/ravendb/discussions/18564); [cluster transactions](https://docs.ravendb.net/7.1/client-api/session/cluster-transaction/overview) |
| **S1** Engine correctness | **Pass at CI size** (3 runs × 100 claimants): 0 oversell; I1 per shard; I2 (a refused claim leaves no document and no hold); the ledger (`sold` = converted − released-with-stock); seats unique; per-buyer counts exact (I4, duplicate refunds included); sweeper killed mid-run (I3). Outcomes: Held 165–188, UnitTaken 39–41, LimitExceeded 73–94. **Also passes on the Community licence.** The full 100 × 1,000 run is a load run (D24) and awaits an owner window: `SPARK_SPIKE_S1_RUNS=100 SPARK_SPIKE_S1_CLAIMANTS=1000` | `tests/MintPlayer.Spark.Commerce.Tests/Inventory/EngineCorrectnessSpikeTests.cs`; engine `libs/commerce/MintPlayer.Spark.Commerce.Inventory/InventoryEngine.cs` |
| **S2** Engine throughput | **Harness ready, not run** (load spike, D24). `SPARK_SPIKE_S2=1 SPARK_SPIKE_RAVEN_URL=http://localhost:8080` runs K ∈ {1, 8, 32, 64}, with and without the chain cursor | `EngineThroughputSpikeTests.S2_holds_per_second_by_shard_count` |
| **S3** Sweeper and live availability | **Pass.** Lapsed holds show as available 509–566 ms after expiry with grace 500 ms and **no sweeper**; a lapsed hold is taken over and its owner's convert returns `Expired`; the sweeper only prunes. Default grace set to 2 s (≤5 s criterion) | `EngineThroughputSpikeTests.S3_…` |
| **S8** PBKDF2 cost | Server (.NET, this 8-core desktop): 50k iterations 7.3 ms, 100k 17.9 ms. WebCrypto (Node 24): 50k 10.9 ms, 100k 31.6 ms. **Choose 50,000 iterations of PBKDF2-HMAC-SHA256**: server ≤10 ms; the gate peak of 20 scans/s costs ~150 ms CPU/s. The low-end Android ≤300 ms check needs a device (owner) at M15 | scratchpad `s8/pbkdf2.cs`, `s8/pbkdf2.mjs` |
| **S0** Demo-app feasibility | **One app is feasible.** Discovery, composition, model sync/verify and security sync handle both library sets in one App_Data. The risk is name overlap between libraries: the same CLR simple name **silently** shares a collection (`SparkDocumentStoreConventions.cs:79-83`); the same custom-action class name silently loses (F11). Library types get distinct names, and a uniqueness test guards the commerce assemblies until X9 | `SparkModelLayers.cs:108-136`; `SparkModelShape.cs:122-127`; `ActionsResolver.cs:44-47` |
| **S10** `ISaveReplacement` | **Feasible.** Hook at `DatabaseAccess.cs:390`, beside MCP propose's `checkOnly` cut: all gates and the base-etag check have passed and nothing is in the session yet. Return a receipt (never throw, `:406-413` evicts); 202 "submitted for approval". **Store the proposal as wire JSON text:** a RavenDB field loses the AsDetail type split (Newtonsoft store vs. STJ converter, `PersistentObjectAttributeJsonConverter.cs:16-69`); the MCP plan precedent is `McpPlan.cs:81-82`. Apply through the internal save path with `Etag = base`; a stale base gives 409 already (`:343-347`, `:785,826`) | Desk spike |
| **S11** Name-collision analyzer | **Feasible; SPARK057–062 are free** (highest in use: SPARK056). `LibraryLayersReader` reads referenced layers; the generator compiles the runtime layering engine. Report 057/058/062 from symbol actions, 061 from a syntax-node action. 059/060 read synchronize-written JSON, so they are **warnings** (an error would fail the build synchronize needs). Further new ids start at SPARK063 | `LibraryLayersReader.cs:36-67`; `ValueObjectCompletenessAnalyzer.cs:76-88` |
| **S16** Visitor identity | **Feasible.** Anonymous POSTs skip antiforgery today (`SparkAntiforgeryMiddleware.cs:154-157`), while anonymous browsers already hold `XSRF-TOKEN`. Data Protection keys live in RavenDB. Merge hooks into `SparkSignInManager.SignInWithClaimsAsync` (`:126-139`) plus a core fallback, exactly once via compare-exchange. **The visitor is never an authenticated principal** | Desk spike |
| **S17** Live topics | **Feasible.** Retained topic bytes per hub, sent with no per-viewer scope; `SparkLiveViewer` frozen at the handshake goes into `LiveQueryArgs`; `serverTime` in `hello`/`ping`/topic frames; lifetime deadline jittered against reconnect storms; private streams refused for IP-keyed callers. Topics bypass row security, so they are declared and documented as public | `LiveConnection.cs:236-262,354`; `LiveHub.cs:266-286` |
| **S19** Meta tags | **Feasible, one middleware for production and the dev proxy:** a startup filter (precedent `SparkServiceWorkerCacheHeaders.cs:16-60`) rewrites index.html on matched routes. Validated against SeoTester's pure `PageChecks.Evaluate`. Caveat: ngsw serves cached index.html to installed PWAs, so pages also set tags client-side | `apps/SeoTester/SeoTester.Library/Checks/PageChecks.cs:36,48-67,111-118,214-222` |
| **S21** Rounding | **Pass: 40,000/40,000** (10k orders × {Gross, Net} × {per group, per line}), 0.7 s. Integer cents; each division rounded half away from zero once; order discounts pro rata by largest remainder; cumulative-share refunds; credit notes mirror invoice lines, **strategy H** (clamp per line, then nudge group VAT a cent at a time). Group-first re-allocation **rejected** (161–193 failures, not monotone). BR-CO-17: Net pricing with per-group VAT is exact on every invoice, so it is the **B2B/Peppol default**. With Gross pricing, 2,393 of 27,424 groups are 1 cent off; M10 runs the official validator on a 1–2 cent deviation. `Money.RoundDiv` and `Money.Allocate` are the only primitives | scratchpad `s21/Program.cs` |
| **S23** Zone names | Browser `Intl` `longGeneric` is localized per call (Node 24, Chromium 155 and Firefox 150 identical; Safari for the owner). .NET `TimeZoneInfo` is **not**: on Windows it follows the OS language; on Linux, the first culture used in the process wins. **Decision:** client keeps fallback step 2; the server drops it (reference data → IANA-derived English city) | scratchpad `s23/` |
| **S24** `TranslatedString` inventory | Production stores it in **one collection only, `OidcResources`** (CodeCoverage and SeoTester through `ForMcp()`). RavenDB stores `{"Translations":{…},"Key":null}` while the API sends `{"en":…}`. Hyphenated tags break index field names (`IndexNaming.cs:92`) and search parsing (`QueryExecutor.cs:2163-2166`). Client and server fallbacks differ. No equality, and no ng-spark renderer. Migrations: `OidcResources` (production), Fleet `Cars`, `LookupReferences` | Desk spike (178 C# lines, 181 TS lines, 38 test files) |
| **S7** Seat-map rendering (M14, 2026-10-19) | **Pass with the layer cache.** Chromium 155, 50,000 seats (40 blocks × 25 rows × 50), 1280 × 800 canvas at DPR 1.25: with `SeatMapLayer` (one `OffscreenCanvas` bitmap per √2 scale step, 35 % margin) **100–239 fps** across the zoom sweep and the pans at 4–24 px/seat (210 fps at block level); criterion ≥50 fps pan and zoom with level of detail **met**. The plain renderer without the layer cache **misses the criterion below 24 px/seat** (25–34 fps; 86 fps at 24 px/seat): rasterising tens of thousands of seats costs the GPU more than the 6–9 ms of JavaScript. Firefox and the low-end Android check not run (round 4) | `docs/guide-ticketing-venues.md` "S7: rendering 50k seats"; harness `tools/seat-map-perf/build-harness.mjs`; Vitest failure bounds `seat-map/seat-map.spec.ts` |
| **S12** Bot defence (M16, 2026-10-20) | **Fixture- and CI-size-based, not load-run** (round 4). Written as tests: ALTCHA protocol, replay and expiry, and each hosted challenge adapter (Turnstile, hCaptcha, reCAPTCHA v3, Friendly Captcha) against requests and answers taken from its vendor's public docs, with the consent fallback to ALTCHA (items S1, S6; no device timings, no screen-reader runs); **0 admission-token double-spends** under concurrent redeemers, with expiry, phase, identity and forgery refusals (S2); 20 tabs + 3 devices = one entry per identity, and nobody refused behind one IPv4 or one IPv6 /64, 2,000 and 500 identities (S3, S4); the risk policy blocks only on deterministic evidence and never on a country alone; `spark-shuffle-v1` is a uniform, seed-fixed permutation at CI size (S10). Not done: ALTCHA p95 on Android, NVDA/VoiceOver, the red team, TLS fingerprinting, live calibration, the "position within 2 s" fan-out (S4 dropped), and the Stripe/Mollie over-limit refusal criterion (not verified for this row) | `tests/MintPlayer.Spark.Commerce.Tests/Challenges/ChallengeProviderTests.cs`, `Challenges/RiskPipelineTests.cs`, `WaitingRoom/WaitingRoomAdmissionTokenTests.cs` (`Zero_double_spends_under_concurrent_redeemers`), `WaitingRoom/WaitingRoomJoinTests.cs`, `WaitingRoom/WaitingRoomShuffleTests.cs` |
| **S13** Carriers (M11, 2026-10-20) | **Fixture-based** (round 4): no sandbox account, no network. Each adapter runs against a fake handler serving fixtures derived from the carrier's public API docs (PostNL) or its published SDK (bpost), marked as such, and Sendcloud v3 against its docs: labels and CN23 per shipment, multi-collo with a shared main barcode (GB5), customs content with HS code and origin (GB6), service points and lockers, status-code mapping, deduplicated tracking events, and no service that accepts dangerous goods until the host opts in (GC9). Real sandbox fixtures replace them when the owner's accounts exist | `tests/MintPlayer.Spark.Commerce.Tests/Shipping/CarrierAdapterTests.cs`, `Shipping/SendcloudAdapterTests.cs`; fixtures `Shipping/Fixtures/` (README states the sources) |
| **S14** Peppol (M10, 2026-10-20) | **Fixture-based** (round 4). The official CEN EN 16931 and Peppol BIS 3.0 Schematron are XSLT 2.0, which .NET cannot run without Saxon (not MIT), so they are **not run**: the library checks its own **rule subset** (BR-02…BR-11, BR-CO-10/13–17 with the ±1 tolerance, BR-S/AE/IC/G/E/O, preceding-invoice reference, PEPPOL-EN16931-R010/R020) and a host plugs the official validation in through `IEInvoiceValidator`. Written as tests: the UBL it writes passes the subset for categories S, AE (reverse charge), K (intra-EU goods) and G (export); a credit note references its invoice; broken totals and reverse charge without a buyer VAT id are refused. Storecove and Billit run against doc-derived fixtures (submission, idempotency, refusal vs outage, discovery, inbox). "Send and receive acknowledged" on a real access point is open until accounts exist | `tests/MintPlayer.Spark.Commerce.Tests/Invoicing/InvoicingRulesTests.cs` (`S14_*`), `Invoicing/PeppolAdapterTests.cs`; `libs/commerce/MintPlayer.Spark.Commerce.Invoicing/EInvoicing/En16931Validator.cs:9-29` |
| **S15** Mass refund (M9, 2026-10-20) | **CI size, not the 50k load run** (round 4). Written as a RavenDB test with the real inventory engine and a test provider that executes a refund reference once: a mass refund over 12 orders crashes after the provider executed a refund, resumes, refunds nothing twice, keeps exact counters (11 executed, 1 insufficient balance, 13 provider calls; one refund per order in its ledger; every unit back exactly once, I4); the order of a seller who cannot cover its refund (D20) fails as "insufficient balance" and completes on retry (12/12 succeeded) | `tests/MintPlayer.Spark.Commerce.Tests/Checkout/RefundFlowTests.cs` (`A_mass_refund_survives_a_crash_and_resumes_with_no_double_refund_and_exact_counters_S15`), `Checkout/RefundTestSupport.cs` |
| S4, ~~S5, S6, S13, S14~~ | ~~Superseded by round 4~~ S4 dropped (round 4); S5, S6, S13 and S14 became fixture-based (round 4); S13 and S14 results above | — |
| ~~S7, S9, S12, S15, S20, S22~~ S9, S20, S22 | Run at the start of their milestones (reduced per round 4) (~~M14~~, M15, ~~M16, M9~~, M11/M20, M2/M3); they need the components themselves, and S22 needs copies of production document shapes. *(S7, S12 and S15: results above, 2026-10-20.)* | — |

### Round 4 (owner, 2026-10-19): no stress tests, no paid resources

- **No load or stress runs** ("I'm not going to do stress-tests, certainly not on this machine"). The full S1 (100 × 1,000), S2 and S4 (200k on cloud VMs: "No, I don't have any money for that") are **dropped**. S1 stays at its CI size in the regular suite, guarding I1–I5. D23's throughput target (≥150 holds/s) is **not measured**. Its levers are in the code and configurable: shard count K, admission rate, one chain per block group.
- **Provider, carrier and Peppol adapters** are written against the providers' documented APIs and consume configuration values ("You should just write the code and consume the configuration-values"). The owner creates development accounts later, once. S5, S6, S13 and S14 therefore become **fixture-based**: request/response fixtures taken from the providers' public documentation, marked as such, to be replaced by recorded sandbox fixtures when the accounts exist.
- **No Safari and no low-end Android checks** ("I don't have macos, low-end android devices shouldn't be used and can be ignored"). S7's and S8's Android criteria, S12's ALTCHA-on-Android criterion and S23's Safari column are dropped. Browser checks use Chromium and Firefox through the Playwright MCP servers.
- **Production `/license/status`:** the owner allowed reading it through the Playwright MCP, but production RavenDB publishes no host port, so no browser route exists. The S18 conclusion stands on discussion #18564 and the licence's re-activation on 2026-09-28.

### Round 5 (owner, 2026-10-19)

Input: a read-only gap analysis of the three PRDs (2026-10-19; A1–A11 legal, B1–B14 commercial, C nice-to-haves, 13 inconsistencies). Its legal sources are secondary summaries (JD Supra, iubenda, RPC, Sellforte, Prighter, CCPC, PwC, Belastingdienst, CMS, Wikipedia, a GS1 guideline); it states it is not legal advice.

- **D37 (scope and testing):** Owner: "Include everything. But this is a framework for other parties to use, So I may not be able to test it properly". → **All of A, B and C are in scope**; none goes to §9. They become GA1–GA11, GB1–GB14 and GC1–GC11, placed in §4 by package and indexed in §4.21, with checklist lines in the plan. **Testing**, consistent with round 4: adapters and third-party integrations (disputes, settlements, service points, customs, insurance, DAC7 XML, TEDB) are verified with **fixtures taken from the parties' public documentation**, marked as such, plus `.Test` adapters; **legal rules are checked against the official EUR-Lex text** (and the national transposition where a member state has an option) **when each is implemented**, because the gap analysis cited secondary sources. Every place this PRD says "confirm, D37" marks such a check. *Evidence:* owner quote; gap analysis header ("check it against the official EUR-Lex text before encoding a rule").
- **D38 (fuzzy search):** Owner chose **"Fuzzy by default"**. → Every index with searchable fields runs on Lucene, and search is typo-tolerant by default with a **per-field opt-out**; existing search indexes rebuild side by side, **production apps included, on their next deploy** (§8). **Revises X18** (fuzzy was opt-in per field, other indexes kept the server default) and **reference_data_PRD §4.10** (non-fuzzy indexes pinned to Corax); both texts are struck and replaced, not deleted. *Evidence:* the engine is selectable per index on every licence and a Lucene index on a Corax-default server works (measured on 7.2.5, reference_data_PRD §4.10); Corax refuses `Fuzzy` at runtime (X18 evidence).
- **D39 (reference data on this branch):** Owner: "2 days ago I "decided" not to develop or not to seed-data the "reference data" application (countries, cities, streets, ...) - But I think I'll do it anyway, just a basic set, some countries, some regions, some streets, ... Just to be able to test --- All of that is in-scope for the current branch". → The reference-data app is **developed on this branch** (commerce M19, reference-data R0–R14) and ships a **basic seed set**: some countries, regions, places and streets, enough for the address typeahead, the merger-history cases and commerce's E2E. Full imports stay manual local runs (RD2). It is still not deployed by us (K8 revised). *Note on the record:* the logs hold no decision not to *develop* the app; K8 revised (2026-10-17) only dropped its deployment, and WD11/Q-O5 still said "only the reference-data app goes live". Both are now marked superseded (§11, warehouse WD11).
- **D40 (one basket across channels; my design under D37, not separately asked):** the gap analysis's B2 and inconsistency 1: D2's "a basket never mixes channels" contradicts the festival scenario (§3.2, D22), where a buyer would pay twice for a ticket and a T-shirt. → `Basket:MixChannels` (off by default, on in the festival demo): a basket may hold lines of several channels; **each line keeps its channel's rules**; **one order and one payment**, with the shortest payment window and the intersection of methods; a failed `OnPayment` line is refunded alone by default; mixed sellers only with a split-capable provider. Ticket add-ons work without it. Details in §4.2. *Evidence:* research §5.1 (pretix add-ons); the gap analysis's survey of pretix, Weezevent and Eventbrite add-on flows.
- **Inconsistency resolutions (my edits under D37, recorded so the log stays the record):**
  - withdrawal is **unconditional** (GA1): §4.7, §4.10 and D20 corrected; the refund includes the standard delivery cost and may be withheld until the goods return or dispatch is proven (CRD Art. 13), which is how I4's shipped-goods exception and §4.9's "refund on receipt" now read;
  - **"dispute lost"** added to I4's return paths (GB1);
  - **`FinalPrice`** defined once (§4.4) and used by display, the resale cap and the Omnibus prior price;
  - **retention floored per document type**, invoices never altered, floors marked "confirm per country" (§4.8, §4.15);
  - the waiting list's activation rule is the **ticket mode**; shops get **back-in-stock** (GB11);
  - **ticket transfer erases the previous holder's data** from the ticket (§4.11);
  - the **hold timer** gets a ≥20 s "More time" affordance up to `Hold:Max`, with the real-time/essential exception argued beyond it (WCAG 2.2.1, §4.14); the presence check rejoins at the previous rank (§4.12);
  - stale status lines fixed (header, §0, companions); M0 now reads S0–S24;
  - round-4-dropped stress and cloud references marked in §7 (S1, S2, S4, environments), D23, D24, §8, §11 Q-O3, §12 and §14;
  - K10 moved above K11 in the constraint table.
- ~~**Round 5 is the last round so far.**~~ *(Round 6 follows.)* Open decisions Q-O1–Q-O4, Q-O8 and Q-O9 remain open (Q-O3's recommendation updated because S2/S4 were dropped).

### Round 6 (owner, 2026-10-19)

Three questions the M6 Payments work raised.

- **D41 (MixSellers vs D5):** *Asked:* both split mechanisms (Mollie routing, Stripe separate charges + transfers) route the buyer's money through the platform's account, which conflicts with D5 ("in connected mode the platform never holds third-party funds"); should `Basket:MixSellers` be dropped, refused, or allowed? Owner chose **"Allow via split, opt-in"**. → `MixSellers` works when the host opts in (`Basket:AcknowledgeSplitMerchantOfRecord=true`) **and** a registered provider supports split; the platform is then the **merchant of record for that order**, and the guide warns about the licensing/KYC consequences. Off by default. Startup refuses `MixSellers` without the opt-in or without any split-capable provider (revises the D40 rule "every provider must split"); Payments refuses a split payment unless the opt-in is on and the provider can split. D5 stands for every single-seller order. §4.2, §4.5 amended. *Evidence:* owner preference.
- **D42 (Mollie chargeback finality):** *Asked:* a Mollie chargeback is never decided by the provider (it is reversed or it stays); when does an undecided one count as lost? Owner chose **"After 120 days"**. → `Disputes:ChargebackFinalAfter`, default 120 days, matching card-scheme dispute windows; configurable; the seller may accept it as lost earlier. §4.5 amended. *Evidence:* owner preference; Mollie's public docs state no deadline.
- **D43 (DAC7 block under Connect):** *Asked:* under Connect the provider pays sellers out directly, so "missing DAC7 data blocks payouts" (§4.19, GA6) cannot be enforced by the platform without touching the provider's payouts; what does the block do? Owner chose **"Block selling"**. → After the reminders run out (and the minimum wait), the seller's **sales close**: `ISparkSellers.CanSellAsync` refuses with `commerce.seller.dac7Blocked` until the data is complete; provider payouts are not touched, and `CanReceivePayoutsAsync` no longer carries the DAC7 reason. §4.19 amended. *Evidence:* owner preference.
- **D44 (withdrawal start for goods delivered separately; my implementation choice under D37, not separately asked, 2026-10-20):** CRD (Directive 2011/83/EU) Art. 9(2)(b)(i): for multiple goods ordered in one order and delivered separately, the withdrawal period starts on the day the consumer acquires physical possession of the **last** good. → One period per order, not one per line: `RefundMath.WithdrawalPeriodStart` is the latest `DeliveredAt` of the order's physical-goods lines, and null (the right stays live, Art. 9(3)) while any of them is undelivered; refused lines do not count. Points (ii) (one good in several lots or pieces: the last lot) and (iii) (regular delivery over a period: the first good) are not modelled; a host selling those needs its own rule. *Evidence:* EUR-Lex text of Art. 9(2)(b); `libs/commerce/MintPlayer.Spark.Commerce.Checkout/Refunds/RefundMath.cs:342-352`; test `RefundRulesTests.The_withdrawal_period_starts_at_the_last_delivery_of_the_order_CRD_9_2_b_i`; `docs/guide-commerce-refunds.md:80`.
- **D44, shop side (M12, my implementation choice under D37, 2026-10-20):** Shop reports a line to Checkout (`ISparkCheckoutRefunds.MarkDeliveredAsync`) only once **every unit of it is delivered**, at the delivery of its last parcel, so a line split over several parcels counts from its last parcel (the case of Art. 9(2)(b)(ii), lots or pieces, for one line); the order-level start stays Checkout's D44 rule, **across every seller of the order** (one order, one period, also in a marketplace basket). Carrier delivery is the source (Shipping's `Delivered` event, or `ISparkShopOrders.MarkDeliveredAsync` from a warehouse or a host's own carrier); a buyer's word never sets it. The legal guarantee runs per line from that line's delivery (Directive (EU) 2019/771 Art. 10(1)). Art. 9(2)(b)(i)/(ii) and 2019/771 Art. 10 are **unconfirmed** against EUR-Lex (fetch blocked; the lead verifies). *Evidence:* `ShopShipping.NewlyDelivered` (`libs/commerce/MintPlayer.Spark.Commerce.Shop/Orders/ShopShipping.cs`); `ShopOrders.ApplyDeliveredAsync`; tests `ShopShippingTests.A_line_split_over_parcels_is_delivered_with_its_last_parcel`, `ShopFlowTests.Delivery_is_reported_per_line_once_all_its_units_arrived`.
- **X18 as built (2026-10-20):** an index without a searchable field (and without a `[Fuzzy]` opt-in) keeps the **server default** engine; Spark does not pin one. This supersedes the X18 row's "pinned explicitly so a licence upgrade never switches it" and "registers the engine explicitly on every index" (§4 table, not edited here); reference_data_PRD §4.10 is corrected to match (its RD11). *Evidence:* `SparkSearchEngine.Apply` skips every task without a searchable field or an opt-in (`libs/spark/MintPlayer.Spark/Indexes/SparkSearchEngine.cs:126-157`); no Corax pin in `libs/refdata`.
- **S9 offline scanner (M15, 2026-10-20; fixture/CI size per round 4, tests written, not run until M22):** **feasible, design kept.** *Size (desk, Node 24.15):* a 50,000-ticket list in the `SparkScannerEntry` wire shape is **297 B/ticket, 14.2 MiB raw, 3.4 MiB gzipped**, in 10 pages of 5,000 (`Scanner:ListPageSize`); the device stores it in IndexedDB (`ng-spark/offline`, `navigator.storage.persist()` requested), a `structuredClone` of all 50k entries took 310 ms. One WebCrypto verification at the production 50,000 PBKDF2 iterations took **20.5 ms** on this (busy) desktop (S8 measured 10.9 ms idle), so an offline scan stays far below a gate's pace. *Double entry:* the server recomputes each ticket's presence per list and date **in time order over every device's scans** (`CheckInRules.Recompute`), so a ticket admitted on two offline devices is recorded twice and the **later** entry is flagged, **whichever device syncs first**; earlier gate-log rows are re-flagged when an older scan arrives; a re-sent scan id changes nothing; device clocks ahead of the server by more than `Scanner:MaxClockSkew` (5 min) are clamped. Changes after the download (revocations, freezes, reissues, other devices' entries) reach a device through `since`-cursor syncs (truncated pages answer the last change's instant), every 30 s, on reconnect, and at once through the public live topic `ticketing-scanner:{eventId}` (a timestamp only, X2). A revoked or expired device (409 `deviceRevoked`) and a device past `WipeAfter` delete their list, queue and session. **Not measured:** a real 50k download over a gate's network and on a phone (round 4: no device labs). *Evidence:* `libs/commerce/MintPlayer.Spark.Commerce.Ticketing.Tickets/CheckIn/TicketCheckIns.cs`, `CheckIn/CheckInRules.cs:83-140`; client twin `libs/node_packages/ng-spark/ticketing/tickets/src/check-in-rules.ts`, engine `src/scanner/ticket-scanner.ts`; tests `TicketCheckInFlowTests.S9_a_double_entry_across_two_offline_devices_is_flagged_whichever_syncs_first`, `CheckInRulesTests.Two_offline_devices_give_the_same_flags_whichever_syncs_first`, `OfflineListSpikeTests` (2,000 tickets by default; `SPARK_SPIKE_S9_TICKETS=50000` for the full list), vitest `ticket-scanner.spec.ts` ("S9: stores a 50,000-ticket list in pages and wipes it"); desk script scratchpad `m15/s9.mjs`.
- **M15 PDF library (Q-O9 still open):** tickets render with the package's own minimal PDF writer (`Pdf/TicketPdfWriter.cs`: text, rectangles, QR modules, JPEG), as Invoicing does (`MinimalPdf.cs`); no third-party PDF library is referenced. When W17 picks a library, `ITicketPdfRenderer` is the seam. *Evidence:* `libs/commerce/MintPlayer.Spark.Commerce.Ticketing.Tickets/Pdf/`.

---

## 14. Verification and acceptance

- **I1–I5 hold:** ~~S1 passes~~ S1 passes **at CI size** (the full-size run was dropped in round 4), and a scaled-down concurrency test in the regular suite keeps guarding them, including the round-5 return paths (withdrawal, refused mixed-basket line, dispute lost).
- **E2E on the demo app:**
  - shop purchase in `OnPayment`, including the race-loser refund;
  - ticket purchase through the waiting room with a hold, the PaymentPending extension and a late-payment re-claim;
  - a ballot sale end to end;
  - PreSelect at admission with clamping;
  - transfer and face-value resale with reissue;
  - offline scanning with a double-entry flag;
  - a mass refund with a mid-way failure and resume;
  - **round 5:** a festival order with a ticket and a T-shirt in **one payment** (D40), and the `RefundLine` path when the T-shirt loses its race;
  - **round 5:** a guest withdraws through the withdrawal function from the signed order link: confirmation step, receipt mail with date and time, refund including standard delivery, withheld until the test carrier's return is received (GA1);
  - **round 5:** "Order with obligation to pay" and the accepted document versions on the order (GA2); a reduction shown with its 30-day prior price (GA3);
  - **round 5:** a test-provider dispute: tickets frozen at the scanner, then lost → revoked and counters returned once (GB1);
  - **round 5:** a weekend pass checked in on two days with re-entry, and refused at a VIP zone list (GB3); a comp issued from a guest list (GB7);
  - **round 5:** consent refused → ALTCHA instead of a third-party challenge (GA9); the hold's "More time" button (GA10);
  - **round 5:** **axe** finds no WCAG 2.1 AA violation on the storefront, basket, checkout, waiting room, seat picker list, withdrawal function and consent banner (GA10).
- **Legal rules (D37):** each GA item's guide cites the EUR-Lex article it was checked against; any rule that could not be confirmed is listed as such in the guide.
- **Adapters (D37):** every round-5 adapter feature has public-documentation fixtures (marked) and a `.Test` implementation exercised in CI.
- **Search (D38):** a misspelled product or event name finds it; an opted-out field does not match fuzzily; the production post-deploy check of §8 passes.
- **Production migrations** (D26, D28) verified after deploy (§8).
- **Browser checks** of the storefront, editor, waiting room and scanner through the Playwright MCP tools.
- **Versions:** every touched `libs/` csproj gets a preview bump; new packages start at `11.0.0-preview.1`; `@mintplayer/ng-spark` gets a minor bump (22.42.0 → 22.43.0). No major changes (repo rule).
