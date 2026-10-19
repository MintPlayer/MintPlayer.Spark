# Commerce suite (#521): PRD and plan

**Status:** research and grilling round 1 complete (2026-10-17). Spikes not started.
**Issue:** [#521 "Ticket sale platform"](https://github.com/MintPlayer/MintPlayer.Spark/issues/521)
**Companions:**
- [commerce_research.md](commerce_research.md): the evidence, with file:line and sources.
- [commerce_plan.md](commerce_plan.md): the order of work.
- `reference_data_PRD.md`: the reference-data app (K8). Pending its own research and grilling.
- `warehouse_picking_PRD.md`: the warehouse order-picking plugin (D13). Pending its own research and grilling.

> The decision log (§13) is the record. Where the body and the log disagree, the log wins; fix the body.

---

## 0. Status

| Item | State | Date | Record |
|---|---|---|---|
| Research: plugin packaging, framework capabilities, RavenDB mechanics, domain, PRD conventions | ✅ | 2026-10-17 | research §1–§5, §10 |
| Research: approvals inventory, bot mitigation, persistent-object name identity | ✅ | 2026-10-17 | research §6, §8, §9 |
| Grilling round 1 (Q1–Q32 + defaults) | ✅ | 2026-10-17 | §13 Round 1 |
| Reference-data app: research | ⏳ | — | K8 |
| Warehouse plugin: research, grilling, PRD | ⏳ | — | D13 |
| Spikes S1–S22 | ⏳ | — | §7 |
| Milestones M0–M22 | ⏳ | — | §10, plan |

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
| K8 | A new **reference-data app** (countries, languages, administrative levels, places with merger and split history, streets; localized and romanized; public typeahead API; deterministic seeding). It has its own PRD; commerce consumes its addresses (§4.18). ~~Live-deployed~~: **not deployed by us**. Owner: "i don't think I'll be deploying this reference data app to my vps. If the code is written and can work properly, that would be great already". Any host can run its own instance. |
| K9 | Redesigns are allowed, including of production data and deployment. Every VPS change they imply is listed in §8. |
| K11 | **Standard web platform APIs first.** Whatever a W3C/WHATWG browser API can do is done with it, not with a library or server round trip. Where support is partial, use a polyfill that implements the *standard* API shape, so it can be dropped later, and document a browser-support matrix per feature. Examples in this PRD:<br>• `Intl.DateTimeFormat` (X16) and `Intl.NumberFormat` (money, X4);<br>• `Intl.RelativeTimeFormat` (countdowns), `Intl.Collator` and `String.prototype.normalize` (accent-insensitive client filtering);<br>• `Intl.DisplayNames` (localized country, language, script and currency names);<br>• WebCrypto PBKDF2 (tickets), WebAuthn (passkeys);<br>• Service Worker + Cache Storage (scanner PWA), Push API + Notifications (queue turn);<br>• `BroadcastChannel` (one live socket shared by all tabs, and one queue entry across tabs, D29), Page Visibility (presence check, D25);<br>• Screen Wake Lock (scanner), Web Share (event links), `BarcodeDetector` (native where present, WASM ponyfill with the same API otherwise). |
| K12 | **Prefer source generators over hand-written boilerplate.** Wherever a generator significantly reduces the code we write (registration, options binding, contracts, constants, typed clients), use one, incremental. Code fixes live in the generator assembly that raises their diagnostic, never in a separate `.CodeFixes` assembly (owner, 2026-10-05; `MintPlayer.Spark.SourceGenerators.csproj:53-54`). Where a Roslyn generator can't do the job (TypeScript output, data tables shared by C# and TS), a build-time tool with a CI diff check does it instead. The study's recommendations are folded into §5. |
| K10 | **Time zones are handled in one place.** All time-zone logic lives in core-framework built-ins: `IRequestTimeZoneResolver`, X14 recipient locale, X16 `<spark-date-time>` and `ISparkDateTimeFormatter`. Libraries and apps never convert or format zones themselves. This is enforced (X16). |

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
| Festival: tickets and merchandise in one app | either | `tickets` + `shop` | for tickets |

The demo app (D22) runs the festival scenario, with both systems hooked up.

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
    }
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
| `MintPlayer.Spark.Commerce` | `commerce` | Channels and bounds, seller profiles and registration, fees, price display, audit | Organizations, Approvals | `commerce/core` |
| `MintPlayer.Spark.Commerce.Inventory` | `commerce-inventory` | Quotas, claims, sharding, numbered units, chain-fill allocator, per-buyer limits, sweeper, live availability | Commerce | — |
| `MintPlayer.Spark.Commerce.Payments` | `commerce-payments` | Provider interface, merchant modes and Connect onboarding, method catalog, webhooks, idempotency | Commerce, Webhooks | `commerce/payments` |
| `….Payments.Mollie` / `.Stripe` / `.Test` | — | Adapters (D6) | Payments | — |
| `MintPlayer.Spark.Commerce.Checkout` | `commerce-checkout` | Baskets, orders, allocation policies, payment orchestration, refunds and cancellations, linked-identifier limits | Inventory, Payments | `commerce/checkout` |
| `MintPlayer.Spark.Commerce.Promotions` | `commerce-promotions` | Codes, presale access, capacity-blocking codes, waiting list | Checkout | `commerce/promotions` |
| `MintPlayer.Spark.Commerce.Invoicing` | `commerce-invoicing` | Numbering, receipts, invoices, credit notes, VAT categories, VIES, e-invoice export | Checkout | `commerce/invoicing` |
| `….Invoicing.Peppol.<Provider>` (×2) / `.Test` | — | Peppol access-point adapters (D19, Q-O2) | Invoicing | — |
| `MintPlayer.Spark.Commerce.Shipping` | `commerce-shipping` | Carriers, labels, tracking, `IShippingRateProvider`. **No Checkout dependency**, because the warehouse's pack station uses it too. The checkout shipping step lives in Shop | core, Webhooks | `commerce/shipping` |
| `….Shipping.Sendcloud` / `.Bpost` / `.PostNL` / `.Test` | — | Carrier adapters (D14) | Shipping | — |
| `MintPlayer.Spark.Commerce.Fulfilment` | `commerce-fulfilment` | Shop side of the fulfilment contract (D13). The contract itself goes in `MintPlayer.Spark.Fulfilment.Contracts` and the transport is chosen in the warehouse PRD (push + reconcile proposed; no ETL on Community) | Checkout, Fulfilment.Contracts | — |
| `MintPlayer.Spark.Commerce.Shop` | `commerce-shop` | Products, variants (with SKU and GTIN), categories, the checkout shipping-method step, returns and 14-day withdrawal, storefront | Checkout, Shipping, Fulfilment | `commerce/shop` |
| `MintPlayer.Spark.Commerce.Ticketing.Events` | `ticketing-events` | Events → Dates → Sale phases, ticket types, prices, sale policies | Checkout | `ticketing/events` |
| `MintPlayer.Spark.Commerce.Ticketing.Venues` | `ticketing-venues` | Venues, layouts, editor, snapshots, chain order, seat tags, CSV | Inventory, Approvals | `ticketing/venues` |
| `MintPlayer.Spark.Commerce.Ticketing.Tickets` | `ticketing-tickets` | Issuance, delivery lead time, named tickets, transfer, face-value resale, scanner PWA | Events, Checkout | `ticketing/tickets` |
| `MintPlayer.Spark.Approvals` | `approvals` | Decision requests + document proposals (D9, D26) | core | `approvals` |
| `MintPlayer.Spark.WaitingRoom` | `waiting-room` | Rooms, modes, admission tokens, presence check, live position | core (+ Challenges, optional) | `waiting-room` |
| `MintPlayer.Spark.Challenges` (+ `.Turnstile`, `.HCaptcha`, `.Recaptcha`, `.FriendlyCaptcha`) | `challenges` | `ISparkHumanChallenge` (ALTCHA built in), risk-signal pipeline, deceptive measures (D29) | core | `challenges` |
| `MintPlayer.Spark.Sms` (+ adapters, Q-O1) | — | SMS sending for phone verification (D29) | core | — |
| `MintPlayer.Spark.Webhooks` | — | Generic signed or fetched webhooks, raw-body preservation, dedupe; GitHub webhooks re-pointed onto it | Messaging | — |

**Reference sets:**
- **Webshop host:** Commerce, Inventory, Payments (+ a provider), Checkout, Shop, Shipping (+ a carrier), Fulfilment, Invoicing (+ an access point). Optionally Promotions and WaitingRoom (drops).
- **Ticketing host:** Commerce, Inventory, Payments, Checkout, Ticketing.Events / Venues / Tickets, WaitingRoom, Challenges, Promotions, Invoicing.
- **Festival:** the union.

**Coupling rule.** Checkout never references Promotions, Invoicing, Shipping, Fulfilment or Ticketing. Those plug in through Checkout's extension points: pricing steps, order-event recipients, capacity sources. A webshop therefore carries no seat-map indexes or rights (memory: *don't couple unrelated features*).

### 4.2 Configuration model

- **Channels (D2).** Named under `Spark:Commerce:Channels:{name}`. Each has an allocation mode (D1), hold bounds, `MaxPaymentWindow` (D3), `RequireSignIn` (D4), waiting-room modes (D10), refund-policy bounds (D20), fees (default 4), resale switches (D30), and the deceptive-measures switches (D29). Every sellable item belongs to exactly one channel, and a basket never mixes channels.
- **Per-sale values.** Sellers choose values per sale or product inside the channel bounds (D2). Stored values are clamped against the bounds when read, so tightening the configuration takes effect immediately.
- **App-wide settings.** `Merchant` (D5), `Sellers:Registration` (D21), currency rules (D19), and the challenge provider plus its runtime switch points (D29, via `SparkSetting<T>`).
- **Startup validation.** Through `Registry.AddMiddleware`, in the Notifications pattern (`SparkNotificationsExtensions.cs:95,102-121`). Startup **refuses**:
  - `Sellers:Registration=Open` with `Merchant=Platform`, unless `AcknowledgeOpenPlatformMerchant=true` (D21);
  - a `RandomizedPreQueue` or `Ballot` mode in a channel without `RequireSignIn`, unless overridden (D10);
  - a deceptive measure enabled without its explicit trigger setting (D29).

  It **warns** on a per-buyer limit in a channel that allows guests (D4).
- **Precedence.** Configuration beats code. This is Moderation's order (`SparkModerationExtensions.cs:44-49`), chosen because configuration is meant to pick the scenario (K2). Values that decide what gets registered are bound eagerly, as in IdentityProvider (`SparkIdentityProviderExtensions.cs:52`).

### 4.3 Inventory engine (D1, D7, D15, D16, D25, D29)

**Invariants.** Every test and spike checks these:

- **I1 — No oversell.** For every quota *q*, at every instant, `sold(q) + activeHolds(q) ≤ capacity(q)`. `activeHolds` counts only holds with `expiresAt > now` (lazy expiry).
- **I2 — All or nothing.** A claim succeeds or fails as a whole, across every quota shard, seat key, per-buyer key and code quota it touches. It runs as **one cluster-wide transaction**: atomic guards on documents plus compare-exchange values. That is correct on a single node and on a cluster (research §3).
- **I3 — The sweeper is not needed for correctness.** It refreshes displayed availability, prunes, and triggers side effects. Killing it may leave availability stale, never oversold.
- **I4 — Exactly once.** Every release returns each counter exactly once, keyed by claim id (idempotent). The same applies to every return path: expiry, payment failure, refund, cancellation, resale, and late payment after an extension (D16).
  - **Exception for shipped physical goods.** A refund of units **already shipped** returns no quota. Those units become sellable again only when they are **restocked**: the return-disposition step in the shop, or the warehouse's restock event when a warehouse app backs the stock. Units refunded before shipment are released normally.
  - Per-buyer counts are returned on refund in both cases.
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
```

- **Zero-total orders** (free tickets) confirm without a provider (D6).
- **The provider webhook is the source of truth**; the redirect is for the user only. Confirmation is idempotent: a duplicate webhook is a no-op, and a second payment for the same order is refunded (research §4).
- **The buyer cancels at the provider:** the provider payment is cancelled actively and the holds are released at once (D3).
- **Waiting-room-gated phases:** `TryClaim` requires a valid admission token (§4.12).
- **Linked-identifier limits (D16, D29).** Account, visitor, e-mail and address are checked at claim. The card fingerprint is checked at payment:
  - Stripe exposes it before confirmation, so the order is refused before any charge;
  - Mollie exposes it only after payment, so the order is voided or refunded within 60 s (S12).
- **Prices** shown always include every mandatory fee (default 4). Fees are broken down at checkout.

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
  - **`ConnectedSellers`:** each seller links their own account through the provider's onboarding. Selling is blocked until the provider reports the seller verified (D21). A platform fee is configurable. Refunds come from the seller's account, and a "refund failed: insufficient balance" state is handled (D20).
- **Fixtures.** Real adapters are tested against **recorded real responses**: no live provider calls in CI (memory: *parsers need real fixtures*). Sandbox keys are set by the owner in user-secrets and never seen by the assistant.

### 4.6 Promotions and waiting list (D18)

- **Codes.** Effects are: percentage off, fixed amount off, unlock hidden items, and **block capacity** (a quota held by the code). Codes have usage limits, validity windows and once-per-buyer rules. A presale access code is a code that opens a sale phase (D17).
- **Discounts on partial refunds.** A discount is spread pro rata across order lines, which defines both partial refunds and VAT bases (D19).
- **Waiting list.** Capacity freed after a sell-out is offered to the next person in line as a personal, time-limited code (e.g. 24 h). The list activates only when the phase is sold out, its queue has closed, and no holds are outstanding; until then, freed capacity goes to the queue.

### 4.7 Refunds and cancellations (D20)

- **Seller refund policy.** Set per sale or product within channel bounds: none, until X days before, or with a fee.
- **Mandatory flows:**
  - **Event cancelled:** a **mass refund**, batched, resumable, idempotent and showing progress. It respects provider rate limits and handles connected-seller balance failures with retry, notification and a manual fallback.
  - **Event rescheduled:** a keep-or-refund window.
  - **Goods:** the 14-day withdrawal right. Dated leisure services are exempt under CRD Art. 16(l) (research §7).
- **Every refund** produces a credit note (D19), returns quota and per-buyer counts exactly once (I4), and revokes the affected tickets (§4.11).
- **Fees.** Whether service and platform fees are refunded is a channel setting.

### 4.8 Invoicing (D19)

- **Numbering.** Gap-free per issuer, allocated atomically (compare-exchange counter per issuer per year).
- **Documents.** B2C receipts, B2B invoices and credit notes, as PDF.
- **Issuer.** The seller in `ConnectedSellers` mode, the platform in `Platform` mode. The platform also invoices sellers for its fees.
- **VAT.** Categories and rates are defined by the host per country; nothing is hard-coded. B2B buyers' VAT ids are checked through VIES.
- **E-invoicing.** An abstraction producing EN 16931 / Peppol BIS (UBL), with access-point adapters chosen by the host (Q-O2) plus a test adapter. In Belgium this is mandatory for B2B since 2026-01-01 (research §7).
- **Currency.** One currency per seller, and one currency per order.
- **Liability.** The rules are configurable. Defaults are never presented as tax advice.

### 4.9 Shipping, carriers, fulfilment (D12, D13, D14)

- **Shipping methods** come from configuration: a flat rate per zone, a free-shipping threshold, and pickup. `IShippingRateProvider` covers real rate calculation.
- **Carrier adapters:** Sendcloud, bpost, PostNL and a test carrier. They produce labels and receive tracking webhooks (through the generic webhooks, X5). A small shop prints labels itself; with the warehouse app, labels are printed at packing.
- **Fulfilment contract.** A `FulfilmentOrder` plus shipment status, kept generic (lines, quantities, addresses, shipments). It is proven with a stub consumer, and the warehouse plugin (D13) is its first real consumer. Stock levels flowing back from the warehouse are part of the warehouse PRD.
  - **Package split.** The warehouse research proposes a separate tiny `MintPlayer.Spark.Fulfilment.Contracts`, so the warehouse never references Checkout. `Commerce.Fulfilment` would then implement the shop side over it. The warehouse PRD decides.
  - **Stock feed.** The warehouse publishes, per SKU and channel, `OnHandSellable`, `ReservedOtherChannels`, a cumulative `ShippedTotal` and a `Version`. The shop's capacity source sets `capacity = OnHandSellable − ReservedOtherChannels − buffer + ShippedTotal`, which keeps I1 exact even when updates arrive late, out of order or twice. To be validated in the warehouse PRD.
  - **Cancellation** is a status change with an accept/reject handshake, never a delete.
  - **Fulfilment-order ids are unique across shops**, because one warehouse may serve several shops.
  - **Returns for warehouse-backed stock:** the refund waits for the warehouse's receipt of the goods. This is configurable: refund on receipt, or on request.
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

- **Catalogue.** Products with optional variants (size, colour), each variant with its own quota; simple categories for navigation; search through Spark search (`docs/guide-search.md`).
- **Order states.** Paid → Shipped (with tracking) → Delivered.
- **Returns.** A buyer refund request the seller approves. The 14-day withdrawal right is built in.
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
  - Optional named tickets, transfer (with a name change, free or with a fee), and an **official face-value resale** exchange. A resold ticket is reissued with a new secret. Transfers and resale **freeze** a configurable time before doors open.
- **Scanner PWA.**
  - It is built on the **shared** `ng-spark/scanning` and `ng-spark/offline` entry points that the warehouse PRD introduces, so tickets and warehouse use one scanning and offline stack. Camera scanning needs a self-hosted WebAssembly `BarcodeDetector` polyfill, because the native API exists only in Chrome on Android, macOS and ChromeOS (warehouse research).
  - The `scanner` organization role uses an event-scoped device token.
  - Online scans mark tickets used atomically.
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
- **Admission token.** Signed (Spark Data Protection) and single-use, with the spent marker under lazy expiry. It is bound to identity, room and phase. Its time to live is at least the hold plus the payment window. **Hold and checkout APIs refuse requests without it.**
- **Sold out.** The room closes truthfully when no capacity and no outstanding holds remain. While holds are still outstanding, the page says stock may come back.
- **Bot defence (D29):**
  - **Defaults:** the admission chain; one entry per identity; posted and enforced linked-identifier limits; one hold per identity with a growing cooldown for repeated unpaid holds; identity-keyed rate limits with IPv6 /64 grouping (X10); a decision log; truthful messages.
  - **Opt-ins:**
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

### 4.15 Security, privacy, audit (defaults 7, 8, 10, 11)

- **GDPR.** Buyer and attendee data plugs into `ISparkPersonalDataContributor<>` and `ISparkAccountDeletionHandler<>`. Orders and invoices are kept for a retention period set by the host, then anonymised.
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

---

## 5. Framework additions (Spark core and existing libraries, same PR)

| # | Addition | Why | Evidence of the gap |
|---|---|---|---|
| X1 | **Visitor identity.** Signed HttpOnly cookie through Spark Data Protection; `ISparkVisitor`; visitor → account merge on sign-in (basket and queue entry); **antiforgery on anonymous POSTs** when a visitor cookie is present | Guests (D4) need a refresh-safe basket and queue entry; a visitor cookie would otherwise be ambient authority | No guest identity in libs; anonymous callers keyed by IP (`LiveSocket.cs:89-92`, `SparkQuotas.cs:28-31`); anonymous POSTs not CSRF-checked (`docs/guide-controllers.md:74-79`) |
| X2 | **Live broadcast topics** (one server value fanned out with no per-viewer re-read), **server time in frames**, a **private per-identity stream**, and socket caps keyed by identity | Queue at 200k (D23), countdown, admission push | Every change re-reads per viewer (`docs/guide-live-updates.md:31-32`); `LiveQueryArgs` has no user (`LiveQueryArgs.cs:21-48`); 5 sockets per IP (`SparkLiveOptions.cs:48`); `ping` carries nothing (`:157`) |
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
| X18 | **Lucene enabled automatically for fuzzy indexes** (reference-data grilling, RD8). A field or a model search rule marked fuzzy makes Spark's index registration set `SearchEngineType.Lucene` on that index; other indexes keep the server default (Corax on Community). An engine change triggers a deterministic side-by-side rebuild on deploy. An analyzer flags fuzzy queries against unmarked indexes | Typo-tolerant typeahead (reference data) and any app search, on every licence | Corax refuses `Fuzzy` at runtime (`InvalidQueryException: Method 'Fuzzy' is not supported`, memory measurement on 7.2.5); the engine is selectable per index on every licence ([docs](https://docs.ravendb.net/7.1/indexes/search-engine/corax)); an engine change applies only to new or reset indexes |
| X19 | **Library-declared indexes emitted into the app** (reference-data Q-R6, accepted for all three PRDs). Libraries declare index definitions, including maps contributed to a shared multi-map index; the app's generator emits the index classes at build time. This extends `[GenerateIndex]` to hand-written and composed indexes | Owner rule "the RavenDB Indexes ALWAYS live in the application" kept, with no copied code in hosts (K12); one shared multi-map index per library family keeps the index count small (the Community cap of 12 static indexes was lifted on 2024-05-22; see S18) | `GenerateIndexGenerator` already emits only in the application (OutputKind Console/Windows) and reads referenced assemblies (memory, #388/PR #484); a C# multi-map index can only map types it references |
| X20 | **Source-generator programme (K12, accepted 2026-10-17)**, applied across all three PRDs:<br>**1.** D28 names: the names generator emits effective names after renames plus each library's ids; unused constant classes are used or deleted.<br>**2.** **TypeScript contracts from C#:** a build-time tool extending `tools/SchemaGenerator` (`JsonSchemaExporter`) emits checked-in `.ts`, with a CI diff check. Scope: all new endpoints, plus migration of the existing hand-written TS mirrors. Prerequisite: typed records instead of anonymous objects for ng-spark-consumed responses. **Layout (investigated):**<br>• files `libs/node_packages/ng-spark/<entry>/src/generated/contracts.ts` (one per entry point, re-exported) and `apps/<App>/<App>/ClientApp/src/app/generated/contracts.ts`;<br>• namespaces mapped by `[assembly: SparkClientModule(...)]`; an unclaimed type is an error;<br>• committed, with a CI verify step and merge-queue regeneration;<br>• library contracts via `tools/SchemaGenerator contracts [--verify]`, app contracts via the app binary (`--spark-synchronize-contracts` / `--spark-verify-contracts`).<br>**No `MintPlayer.Dotnet.Tools` change needed:** a Roslyn generator can't write `.ts` files (measured). **Polyglot (checked at the owner's request): not for contracts.** Measured against v0.10.1:<br>• `long` → `bigint` while the wire sends numbers;<br>• unions serialize as `{}`, and `[JsonPolymorphic]` can't be added;<br>• TS records are classes and enums are numbers;<br>• global-namespace collisions between libraries;<br>• a second source of truth.<br>It is used only for the **GS1 core** (item 6).<br>**3.** One shared discovery generator, replacing 7 registration generators + `SparkFullGenerator`'s duplicate, and covering row policies, `IReplicaChanged<T>`, approval deciders/handlers and account hooks behind one generated entry point.<br>**4.** `GenerateIndexGenerator` extended: `Money`/`Quantity` companions, sanitized per-language field names (X17), the fuzzy marker (X18), and the **record fix** (it only matches `class`).<br>**5.** An enum-backed lookup generator (G1, decided): `[SparkLookup] enum` → a generated lookup class with keys `lookup.{Enum}.{Member}` resolved through `translations.json` (the existing `Key` mechanism; library translation layers), with the translation-key guard failing on missing or unused keys. It replaces the hand-restated lookup classes and their inline `_TS(en,fr,nl)` labels.<br>**6.** GS1: the AI tables stay data (warehouse Q-W16). The **GS1 core** (element strings, Digital Link, linters, check digits) is written once in **MintPlayer.Polyglot** `.pg` and compiled to C# (internal, behind a hand-written facade) and TS. This applies **only if** a Polyglot release fixing [MintPlayer.Polyglot#73](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73) ships before warehouse WM2; otherwise both parsers are hand-written against GS1 Syntax Engine vectors (owner decision). The regeneration script uses the CLI binary that the `MintPlayer.Polyglot.MSBuild` NuGet restore already puts on disk. Polyglot types never cross the wire.<br>**7.** ETL scripts generated at runtime from replication contracts (complex projections refused at startup); replicas reuse the contract record.<br>**8.** Runtime plugin helpers instead of a generator: public `AddSparkConfigurationSection<T>`, a static `SectionName`, an idempotency helper, a one-per-slot provider helper.<br>**9.** Guards: a translation-key verify test (2 keys missing today: `dashboards.yes`, `common.unexpectedError`); K10 through BannedApiAnalyzers; migration `Version` derived from the class name; an endpoint filter replacing 33 identical `EnsureAuthorizedAsync` try/catch blocks. | Less code written by hand, compile-time completeness, no TS drift | Source-generator study: two TS drifts exist today (`PersistentObjectAttribute.options` missing; 6 of 11 always-sent `EntityPermissions` fields marked optional); `AddSubscriptionWorkers()` and the generated name constants are never called or used; `GenerateIndexGenerator`'s syntax filter matches only `class` (`:72`); `IndexNaming.cs:92` emits an invalid identifier for `zh-Hant`. Not done (not worth a generator): importer parsers, lineage tables, approval logic, typed ids, the 5 bespoke JSON converters, DTO mapping, constant classes, messaging lanes |
| X15 | **po-form edit renderer for a single AsDetail value object** (e.g. an `Address` on a venue, seller profile or order) | Addresses everywhere in commerce (§4.18) | A single AsDetail attribute can never get an edit renderer (`spark-po-form.component.html:381-398`) |
| X14 | **Recipient locale for server-rendered output.** Time zone and culture stored on the account (preference) and captured onto orders at checkout; an `ISparkRecipientLocale` resolver that mails, PDFs and background jobs use to format dates | D33: mails and PDFs must be localized, and background jobs have no request | The guide requires formatting such values in .NET with a resolved zone (`docs/guide-dates-and-sorting.md:336-344`); `IRequestTimeZoneResolver` needs a request (`:291-331`) |

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
- **The 200k queue spike** runs on temporary cloud VMs that the owner provisions and destroys.

| # | Spike | Question | Pass criterion |
|---|---|---|---|
| S0 | Demo-app feasibility | Can one app host both systems, with one App_Data, model sync and security sync? (D22) | `--spark-synchronize-model` and `--spark-verify-model` green with both channels; otherwise fall back to two apps |
| S1 | Engine correctness | Does any interleaving oversell? | **0 oversell** over ≥100 randomized runs × 1,000 concurrent claimants across multi-quota, seats and per-buyer keys; I1–I5 asserted; sweeper killed mid-run |
| S2 | Engine throughput | How many holds per second on one 3-core node? | **≥150 successful holds/s sustained, p95 ≤250 ms** (D23); K ∈ {1, 8, 32, 64} shards; chain-cursor contention measured; otherwise record the ceiling and the lever (shards / admission rate / licence) |
| S3 | Sweeper and live availability | How fast does freed stock show? | Expired holds show as available within ≤5 s; correctness unaffected with the sweeper off |
| S4 | Waiting room at scale (cloud) | Broadcast fan-out and join burst at 200k | Positions refreshed ≤5 s for 200k sockets; admission-worker failover with no double admission; memory per socket recorded |
| S5 | Provider sandboxes | Real Mollie and Stripe behaviour | Per-method expiry, cancel, refund, late webhook, duplicate webhook, card fingerprint timing **recorded as fixtures**; the Stripe held-flow shape decided |
| S6 | Connect onboarding | Mollie Connect and Stripe Connect | OAuth/onboarding, KYC-status updates, application fee, refund from the seller balance; insufficient-balance failure reproduced |
| S7 | Seat-map rendering | 50k seats in the browser | ≥50 fps pan and zoom on a mid-range laptop; a usable low-end Android; level of detail working |
| S8 | Ticket hashing cost | PBKDF2 parameters | Server verify ≤10 ms; WebCrypto verify ≤300 ms on low-end Android; gate peak of ~20 scans/s within 3 cores |
| S9 | Offline scanner | List size and sync | A 50k-ticket list downloads, is stored and wiped; double entry across two offline devices detected on sync |
| S10 | Approvals seams | `ISaveReplacement` with AsDetail, references, concurrency | A proposal of a document with nested rows round-trips; a stale base gives 409 |
| S11 | Name-collision analyzer | Can the analyzer read referenced layers and report at symbol locations? | SPARK057–062 fire on fixtures with working code fixes; no diagnostic on intentional deltas |
| S12 | Bot defence (research §6, items S1–S10 there) | ALTCHA, tokens, identity, shared networks, limits, a11y, red team, TLS fingerprint, calibration, fairness | ALTCHA p95 ≤2 s on a low-end Android; **0 admission-token double-spends**; 20 tabs + 3 devices = 1 entry; 0 genuine users refused behind one IPv4 or IPv6 /64; Stripe refuses an over-limit order before charging, Mollie refunds it within 60 s; NVDA and VoiceOver pass; red team finds no second entry, no hold without admission, no hold over the limit; calibration blocks 0 humans; all clients see their position within 2 s of the start |
| S13 | Carrier sandboxes | Sendcloud, bpost, PostNL | Label created, tracking webhook received; real fixtures recorded |
| S14 | Peppol sandboxes | Two access points | UBL validated against EN 16931 Schematron; send and receive acknowledged |
| S15 | Mass refund at scale | 50k refunds through the test provider with rate limits | Completes; resumes after a crash with no double refund; credit notes and counters exact |
| S16 | Visitor identity | Merge and CSRF | Visitor → account merge keeps the basket and the best queue entry; anonymous POST without antiforgery refused |
| S17 | Live topics | Broadcast + private stream design on `/spark/live` | Topic delivered without a per-viewer read; private stream bound to identity without `HttpContext` in `LiveQueryArgs` |
| S18 | Community feature checklist | Is everything used available on the Community licence (server 7.1.10 in production, client 7.2.6)? | A written checklist: compare-exchange, cluster-wide transactions, subscriptions ≤3 per database, no reliance on the expiration sweep, **no ETL**, one custom analyzer per database, revisions limits, and the **index limits**. RavenDB **lifted** the Community caps (12 static / 24 auto per database) for newly issued licences on **2024-05-22**, and existing licences update when the server reaches `api.ravendb.net` ([discussion #18564](https://github.com/ravendb/ravendb/discussions/18564)); the buy page's footnotes still list the old caps ([pricing](https://ravendb.net/buy)). Read the production server's `/license/status` to confirm which applies. Only if a cap is still present, count the composed app's static indexes and consolidate |
| S19 | Meta tags in production hosting | Does injection work for the production static files and the dev proxy? | Link-preview card validated by SeoTester's OpenGraph checks |
| S20 | Fulfilment transport | The WD1 transport (SyncAction → owner → HTTPS push + digest reconcile; ETL only when licensed), on Community constraints; shared with the warehouse PRD's transport spikes | Stub consumer receives orders; status write-back lands on the owner; replay and app-down scenarios lose nothing and duplicate nothing |
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
| ~~New reference-data app deployment (K8)~~ | — | **not deployed** (K8 revised) | Runs locally and under test; hosts may deploy their own |
| Commerce demo deployment | demo | **not deployed** (WD11) | Runs locally and in CI E2E |
| Resources (RAM, cores, Community licence) | VPS | after S2/S4 | Spike numbers decide; a licence or VPS upgrade only if the measured ceiling demands it. Today: Hetzner CPX32, shared vCPUs, about 3 GB RAM free (`docs/coverage_commit_graph_PRD.md:34`); Community licence expires 2027-03-31 |
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

---

## 10. Milestones (one PR)

Tests run once at the end, batched (repo rule). Each milestone is committed.

| # | Milestone | Depends on |
|---|---|---|
| M0 | Spikes S0–S22 (results logged in §13) | — |
| M1 | Framework additions X1–X8, X10–X13, and defects F1–F6, F10, F11 | S16, S17, S18 |
| M2 | Persistent-object name identity (X9, D28): attribute, analyzer, `$extends`, actions by contract, rename handling, F7–F9; stored names → ids migration | S11, S22 |
| M3 | Approvals library + the four migrations (D9, D26) | M1 (X7), S10, S22 |
| M4 | Commerce core: channels, bounds, sellers, fees, startup validation | M1, M3 |
| M5 | Inventory engine (I1–I5, chain fill, per-buyer limits, sweeper, live topics) | S1–S3, M4 |
| M6 | Webhooks (X5), Payments + Mollie / Stripe / Test, merchant modes | S5, S6, M4 |
| M7 | Checkout: baskets, orders, policies, extension, late payment, linked limits | M5, M6 |
| M8 | Promotions + waiting list | M7 |
| M9 | Refunds and cancellations, mass refund | M7, S15 |
| M10 | Invoicing + Peppol adapters + Test | M7, S14, S21 |
| M11 | Shipping + carriers + Fulfilment contract | M7, S13, S20 |
| M12 | Shop | M11 |
| M13 | Ticketing events, dates, phases | M7 |
| M14 | Venues, layouts, editor, snapshots, approvals wiring | M3, M5, S7 |
| M15 | Tickets, scanner PWA, named / transfer / resale | M13, S8, S9 |
| M16 | Waiting room, challenges, risk pipeline, bot defence, deceptive measures (guarded) | M1 (X1, X2, X12), S4, S12 |
| M17 | Storefront pages + server-side meta tags | M12–M16, S19 |
| M18 | Demo app (both systems) + E2E | S0, M17 |
| M19 | Reference-data app (per `reference_data_PRD.md`) + commerce address integration | its PRD |
| M20 | Warehouse plugin (per `warehouse_picking_PRD.md`), including the cross-app transport and the replication defects F12–F19 | its PRD, M11 |
| M21 | Guides and READMEs for every package | all |
| M22 | Test sweep, browser verification, version bumps, VPS checklist (§8) | all |

---

## 11. Open decisions

- **Q-O1 SMS adapters.** Which providers (K7)? *Recommendation:* Twilio and one EU provider, plus a test adapter, confirmed by a short research item.
- **Q-O2 Peppol access points.** Which two adapters? *Recommendation:* decide from a short research item comparing APIs, pricing and sandboxes (feeds S14).
- **Q-O3 Admission defaults.** Maximum active shoppers and the per-minute rate. *Recommendation:* set from S2 and S4 measurements.
- **Q-O4 Box office / door sales (POS).** Selling at the door through the scanner app? *Recommendation:* ask in round 2; it reuses Checkout with a "cash/card-present" payment method.
- ~~**Q-O5 Deploy the commerce demo publicly?**~~ **Closed by WD11** (warehouse grilling, 2026-10-17): demos stay local; only the reference-data app goes live. Revisit once the spikes measure VPS headroom.
- **Q-O9 PDF rendering.** Tickets, invoices, labels and packing slips need PDFs, and `libs/` has none; SeoTester prints through headless Chromium (warehouse research). Options: a .NET PDF library (licence check needed: QuestPDF's community licence has revenue limits; PdfSharp/MigraDoc is MIT), or headless Chromium (heavy in containers). *Recommendation:* decide by a spike shared with the warehouse PRD (WX6); prefer an MIT-licensed .NET library.
- ~~**Q-O6 Reference-data app.**~~ **Closed:** researched, grilled and written as `reference_data_PRD.md` (RD1–RD8 + Round 2, 2026-10-17).
- ~~**Q-O7 Warehouse plugin.**~~ **Closed:** researched, grilled and written as `warehouse_picking_PRD.md` (WD1–WD12 + Round 2, 2026-10-17).
- **Q-O8 B2B payment on invoice** (bank transfer, days to pay) in OnPayment channels? *Recommendation:* allowed only in OnPayment channels; stock is taken when the transfer is confirmed, and a race loser is refunded.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| One Community node can't reach 150 holds/s | S2 measures; the levers are shards, the admission rate, or an in-memory allocator under a lease with batched commits. The ceiling is documented, not hidden |
| The chain-fill cursor becomes the hot key | One chain per block group; holes-first allocation; S2 |
| 200k sockets exceed one box | Broadcast topics (X2); a polling fallback against a CDN-cacheable counter; S4 |
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
- **D2 (Q2):** Named channels in configuration, each with its own mode and settings, **plus per-sale values chosen by the seller within configured bounds**. A basket never mixes channels. *Evidence:* owner chose C over plain channels.
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
- **D20 (Q20):** Seller refund policies within bounds, plus mass refund, reschedule window and 14-day withdrawal for goods; refund-failure states.
- **D21 (Q21):** `Sellers:Registration = Closed | ApprovalRequired | Open`. ApprovalRequired uses Approvals; connected sellers are gated on provider KYC; Open + Platform is refused unless acknowledged.
- **D22 (Q22):** Owner: "If possible, I think one demo app, with both systems hooked up - Or else option B" (two apps).
- **D23 (Q23):** Stadium scale on one Community node: 50k numbered + 10k standing sold out ≤15 min; 200k queued; ≥150 holds/s, p95 ≤250 ms; zero oversell at 1,000 concurrent; positions refreshed ≤5 s.
- **D24 (Q24):** Engine spikes on the owner's machine, queue spike on temporary cloud VMs. Owner: "localhost:8080 has a developer license" → pin to 3 cores; Community-only features (S18).
- **D25 (Q25):** PreSelectOrder auto-holds at admission, with ranked alternatives and a presence check. Owner: "for small venues, the expidator may want to let the people choose their seats freely, while for stadiums the expidator may want to fill blocks in chains, which makes groups sit together instead of spread out" → chain fill (§4.3).
- **D26 (Q26):** Migrate all four decision workflows, including the production Organizations join request; fix F1–F4 red→green.
- **D27 (Q27):** Libraries ship complete, replaceable pages; server-side meta tags (X6).
- **D28 (Q28):** All five pieces of persistent-object name identity (X9). Owner: "Check if the Spark library can ship an assembly-level attribute that lets application developers specify the name of the json file… not sure how we should act when the developer decides to change this string though… PersistentObjects don't necessarily have to relate to a c# entity", then: "the PersistentObjectNameAttribute isn't necessary, If it's not present, the entity name should be picked. We should probably ship an analyzer that detects colliding PersistentObject names somehow". *Evidence:* research §8.
- **D29 (Q29):** The anti-bot design **plus deceptive measures** (owner's choice), guarded as described in §4.12. *Evidence:* research §6–§7.
- **D30 (Q30):** Named tickets, transfer and face-value resale, opt-in per sale; freeze before doors.
- **D31 (Q31):** Commerce packages grouped under `MintPlayer.Spark.Commerce.*`, generic ones flat; short folders.
- **D32 (Q32):** The full layout editor with CSV import and export.
- **Defaults (accepted):** 1 seller dashboards (Dashboards lib); 2 buyer mails (MailManager) + optional Web Push; 3 accessible seating; 4 price transparency with fees per channel; ~~5 venue time zone~~ (superseded by D33); 6 translatable UI and seller texts; 7 GDPR hooks + retention; 8 money audit; 9 hashed ticket secrets — owner: "use a strong hashing algo like bcrypt or something like that" → PBKDF2-HMAC-SHA256 (native in .NET and WebCrypto; tuned in S8); 10 identity-keyed rate limits; 11 no MCP buying by default.
- **K8 (owner):** the reference-data app, quoted in full in `reference_data_PRD.md` once written.
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
| S4, S5, S6, S13, S14 | Superseded by round 4 | — |
| S7, S9, S12, S15, S20, S22 | Run at the start of their milestones (reduced per round 4) (M14, M15, M16, M9, M11/M20, M2/M3); they need the components themselves, and S22 needs copies of production document shapes | — |

### Round 4 (owner, 2026-10-19): no stress tests, no paid resources

- **No load or stress runs** ("I'm not going to do stress-tests, certainly not on this machine"). The full S1 (100 × 1,000), S2 and S4 (200k on cloud VMs: "No, I don't have any money for that") are **dropped**. S1 stays at its CI size in the regular suite, guarding I1–I5. D23's throughput target (≥150 holds/s) is **not measured**. Its levers are in the code and configurable: shard count K, admission rate, one chain per block group.
- **Provider, carrier and Peppol adapters** are written against the providers' documented APIs and consume configuration values ("You should just write the code and consume the configuration-values"). The owner creates development accounts later, once. S5, S6, S13 and S14 therefore become **fixture-based**: request/response fixtures taken from the providers' public documentation, marked as such, to be replaced by recorded sandbox fixtures when the accounts exist.
- **No Safari and no low-end Android checks** ("I don't have macos, low-end android devices shouldn't be used and can be ignored"). S7's and S8's Android criteria, S12's ALTCHA-on-Android criterion and S23's Safari column are dropped. Browser checks use Chromium and Firefox through the Playwright MCP servers.
- **Production `/license/status`:** the owner allowed reading it through the Playwright MCP, but production RavenDB publishes no host port, so no browser route exists. The S18 conclusion stands on discussion #18564 and the licence's re-activation on 2026-09-28.

---

## 14. Verification and acceptance

- **I1–I5 hold:** S1 passes, and a scaled-down concurrency test in the regular suite keeps guarding them.
- **E2E on the demo app:**
  - shop purchase in `OnPayment`, including the race-loser refund;
  - ticket purchase through the waiting room with a hold, the PaymentPending extension and a late-payment re-claim;
  - a ballot sale end to end;
  - PreSelect at admission with clamping;
  - transfer and face-value resale with reissue;
  - offline scanning with a double-entry flag;
  - a mass refund with a mid-way failure and resume.
- **Production migrations** (D26, D28) verified after deploy (§8).
- **Browser checks** of the storefront, editor, waiting room and scanner through the Playwright MCP tools.
- **Versions:** every touched `libs/` csproj gets a preview bump; new packages start at `11.0.0-preview.1`; `@mintplayer/ng-spark` gets a minor bump (22.42.0 → 22.43.0). No major changes (repo rule).
