# Commerce: promotions, waiting list, sale prices, tracking links

`MintPlayer.Spark.Commerce.Promotions` (alias `commerce-promotions`, ng entry point
`@mintplayer/ng-spark/commerce/promotions`) holds what PRD §4.6 (D18) and round 5 (GA3, GB11, GC2) ask of promotions.
It depends on Checkout; **Checkout never references it** (PRD §4.1). It plugs in through Checkout's own extension points:

| Checkout extension point | Promotions registers | Does |
|---|---|---|
| `ICheckoutPricingStep` (50) | `SalePricePricingStep` | an active sale price as a line adjustment `sale` (GA3) |
| `ICheckoutPricingStep` (100) | `PromotionCodePricingStep` | the code the buyer entered (choice `promotionCode`) and the block codes of wrapped lines; the discount spread pro rata; usage reserved at "Pay" |
| `ICheckoutAdmissionGate` | `PromotionAdmissionGate` | unlock and presale codes for items with an admission scope `promotions:{scope}` |
| `ICheckoutCapacitySource` | `PromotionBlockCapacitySource` | items `promotions:{CODE}\|{itemId}` sold from a code's block |
| `IOrderEventRecipient` | `PromotionOrderEvents` | uses follow the order; offers marked purchased; attribution and commission; freed capacity re-evaluates the waiting lists |
| `IInventoryClaimLapsed` | `PromotionClaimLapsed` | a lapsed hold re-evaluates the waiting lists |

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddCommerceCheckout();
    spark.AddCommercePromotions();
});
```

## 1. Codes (§4.6, D18)

A code has one effect. Codes are unique on the platform (`PromotionCodes/{CODE}`; upper case, `A–Z 0–9 - _`, 3–64
characters). Seller staff (the organization's Owners, Admins and members with the `promotions` custom role) manage
their seller's codes; platform admins (`ManageSellers/Commerce`) manage platform codes (no `OrganizationId`, every
seller's items) and every seller's.

| Effect | What it does | Fields |
|---|---|---|
| `PercentageOff` | a percentage off the eligible lines | `PercentBasisPoints` (1500 = 15 %) |
| `FixedAmountOff` | a fixed amount off the eligible lines, never more than their goods | `Amount` (the basket's currency) |
| `Unlock` | opens hidden items and **presale phases**: a presale access code is an unlock code of the phase's scope (D17) | `Scopes`, or `ItemIds` |
| `CapacityBlock` | holds a quota of one item for the code's buyers | `ItemIds` (one item), `BlockQuantity`, `ValidUntil` (required) |

Every code takes `ValidFrom`/`ValidUntil`, `MaxUses` (orders, or admissions for an unlock code), `OncePerBuyer` (one use per
buyer: the account when signed in, else the guest's e-mail address, else the guest's visitor identity; checkout passes
them in `CheckoutPricingContext.BuyerEmail`/`VisitorId`) and `MinimumOrderTotal` (discounts). ⚠️ A guest key is only as
strong as its identifier: a guest who uses a new address and clears cookies counts as a new buyer; require sign-in on
the channel when that matters.

```http
POST /spark/commerce/promotions/admin/codes/save
{ "code": "SUMMER26", "organizationId": "organizations/7-A", "effect": "PercentageOff", "percentBasisPoints": 1500,
  "validUntil": "2026-09-01T00:00:00Z", "maxUses": 500, "oncePerBuyer": true }
```

The buyer enters a code through checkout's choices: `POST /spark/commerce/checkout/basket/choices` with
`{ "choices": { "promotionCode": "summer26" } }`. A refusal comes back as a checkout refusal whose code is one of
`PromotionRefusals` (`promotionCodeInvalid`, `promotionCodeExhausted`, `promotionCodeAlreadyUsed`, …); the client
translates `commercePromotions.refusal.{code}`.

### Usage limits are exact (X12)

A code's uses are one compare-exchange counter, a buyer's use of a code a single-owner hold (`ISparkClaims`). The
pricing step checks them on every quote and **reserves** one on the final pricing of a placement
(`CheckoutPricingContext.Placing`), keyed by the placement's lowest line id (checkout keeps line ids from basket to
order). The reservation has a deadline (`UsageReservation`, 30 minutes), so a placement that fails after pricing leaves
nothing behind. The order-event recipient extends it to the payment deadline on `Placed`, makes it permanent on
`Confirmed`, and releases it on `Cancelled`/`Expired`/`Voided`; a full refund gives it back only with
`ReleaseUseOnRefund`. A late payment (D3) takes the use again if the limit still allows.

⚠️ Checkout's contract for pricing steps says "side-effect free". The reservation is the one exception, and it expires
on its own, so a quote never leaves anything behind (only `Placing` reserves).

### Discounts are spread pro rata (D19)

A discount is computed once over the eligible goods (gross price plus earlier adjustments; mandatory fees are not
discounted), then spread over the eligible lines pro rata to their amounts with `Money.Allocate` (largest remainder,
X4/S21), so the parts sum exactly. Each part is a **line adjustment** (`Source = promotions`, `Code` = the code), so a
partial refund of a line gives back that line's share, and `PromotionRules.PerUnit` splits a line's share per unit for
a refund of some of its units.

⚠️ **VAT base.** Checkout stores a line's `Net`/`Vat` from the final price before adjustments
(`CheckoutService.ToOrderLine`). A discounted line's VAT base must be its total after adjustments; Checkout (or
Invoicing) has to recompute `Net`/`Vat` from `Total` with the line's VAT rate (reported to the lead).

### Unlock and presale codes

A capacity source (Shop for a hidden product, Ticketing.Events for a presale phase) gives the item an admission scope
`promotions:{scope}` (`PromotionItemIds.Scope("presale/phase-1")`). Checkout then requires an admission token for it and
asks the gates; Promotions' gate admits when the token is a live unlock code that opens the scope (or names the item).
With `MaxUses`/`OncePerBuyer` the admission is counted once per buyer and item when checkout consumes it.

⚠️ An item can carry one admission scope. A phase that is both waiting-room gated and presale-coded needs the waiting
room's scope during the public sale and the promotions scope during the presale (the capacity source switches it).

### Capacity-blocking codes

Saving a `CapacityBlock` code moves `BlockQuantity` units of the item out of public sale: a converted inventory claim on
the item (so the public quotas count them as sold) and a quota of the block's own (`promotions/blocks/{guid}`). The
buyer adds the **wrapped item** `promotions:{CODE}|{itemId}` (`GET /spark/commerce/promotions/code?code=` lists it as
`blockedItemIds`); Promotions' capacity source resolves the inner item through the other sources and makes the line
claim from the block quota. When the code ends (`ValidUntil`) or is deactivated, the job closes the block and returns
the unsold units to public sale (`ReturnAsync`, keyed, exactly once, I4); refunds after the close keep flowing back for
400 days. Every block change is written as a pending step first and finished idempotently, so a crash leaves units at
worst out of sale until the next pass, never sold twice (I1).

Seats (numbered units) cannot be blocked this way. A wrapped item id is what the order line carries (`ItemId`); report
by `PromotionItemIds.Inner(itemId)`.

## 2. The waiting list: ticket mode (§4.6, D18)

```http
POST /spark/commerce/promotions/admin/waiting-lists/create
{ "itemId": "ticket-types/standing", "organizationId": "organizations/7-A", "queueScope": "phases/general" }
```

Buyers join with an account (`POST …/waiting-lists/join { listId, quantity }`, at most `MaxQuantityPerEntry`).

**Activation rule:** the list activates only when the item is **sold out**, its **queue has closed**, and **no holds
are outstanding** (`PromotionRules.ShouldActivate`). Until then freed capacity goes to the queue or the public. The
queue's state comes from an `IWaitingListQueueState` (the waiting room does not reference Promotions: a host or a bridge
implements it over `ISparkWaitingRooms`); without one, staff mark the queue closed (`…/admin/waiting-lists/close-queue`).
A list without a `queueScope` has no queue.

Once active, every unit freed from public sale (a refund, a lapsed hold, a closed block) moves into the list's block,
and the block's spare units are offered **strictly in order of joining** as personal codes: a `CapacityBlock` code
assigned to the entry's account, one use, `MaxQuantityPerOrder` = the entry's quantity, valid `OfferValidity` (24 h by
default). The offer is mailed (`SparkPromotions/WaitingListOffer`). An offer that lapses goes to the next entry, never
back to the public, while the list is active. Closing the list (`…/admin/waiting-lists/close`) lapses open offers and
returns the spare units.

The list re-evaluates every 30 seconds (`BlocksSchedule`), on every refund, cancellation and lost dispute, and when the
inventory sweeper reports a lapsed hold.

## 3. Back in stock: shop mode (GB11)

A buyer subscribes to a variant: `POST …/back-in-stock/subscribe { itemId, email? }`. A signed-in buyer uses the
account's confirmed address; a guest's address is confirmed by double opt-in (`SparkPromotions/BackInStockConfirm`,
deleted after `ConfirmationWindow`, 48 h). One subscription per item and address.

When the item's availability rises from zero, the job mails the oldest subscribers, `ceil(restocked × BatchFactor)` of
them (default factor 2, capped by `MaxBatch`). **No code and no reservation:** the first payer wins (OnPayment).
Subscriptions lapse after `SubscriptionLifetime` (90 days) and are erased with the account (GDPR Art. 17). Each mail
carries a one-click "forget my request" link (`…/back-in-stock/unsubscribe` with the mail's token).

The notice is a service mail the buyer asked for, not marketing, so it needs no consent. A host whose counsel reads it
otherwise sets `BackInStock:RequireMarketingConsent`: the mail then goes only with `marketing-mail` granted (X21), and
startup refuses without the consent primitive. ⚠️ Confirm the reading of ePrivacy Art. 13 per country (D37).

## 4. Seller sale prices and the Omnibus prior price (GA3)

A sale is a price change with a validity window, not a code:

```http
POST /spark/commerce/promotions/admin/sale-prices/schedule
{ "itemId": "products/12-A", "salePrice": { "amount": 79.00, "currency": "EUR" },
  "validFrom": "2026-11-27T00:00:00Z", "validUntil": "2026-12-01T00:00:00Z" }
```

- The item is resolved through checkout's capacity sources (it must be on sale); the sale must be lower than the
  current price and may not overlap another sale of the item.
- **Price history.** The job appends the sale's start (an announced reduction, with its prior price) and end to
  Commerce's price history (`ISparkPriceHistory`), so later reductions see it.
- **Checkout.** The pricing step (50) adjusts a line to the sale price (`ApplyInPricingStep`). A capacity source may
  instead price the sale itself with `ISparkSalePrices.ApplyAsync` (then turn the step off).
- **Display.** `GET /spark/commerce/promotions/sale-price?itemId=` (or `ISparkSalePrices.DisplayAsync(item)`) answers
  the final price, and whether the reduction may be **announced**. It may be announced only with the **prior price**:
  the lowest final price of the 30 days before the reduction started (PID Art. 6a(1)–(2)). A progressive reduction
  (a sale that follows another without a gap and goes lower) keeps the reference of its first step (Art. 6a(5)). The
  percentage is computed on the prior price and rounded down. Without a prior price (no history in the window), the
  sale price is shown as the plain price: no strike-through, no badge, no percentage. `<spark-sale-price>` renders
  exactly that.
- **Perishable goods** (Art. 6a(3), a member-state option): `perishableExempt` per sale; no prior price is shown.
- **Codes the buyer enters** are personal discounts, not announced reductions, and carry no prior price.

⚠️ **D37.** Art. 6a's text, the member-state options (6a(3) perishables, 6a(4) goods on the market for less than 30
days) and the Commission guidance (2021/C 526/02) on percentages and progressive reductions must be confirmed against
EUR-Lex and the national transpositions. The agent that wrote this could not fetch EUR-Lex.

## 5. Tracking links (GC2)

```http
POST /spark/commerce/promotions/admin/links/save
{ "ref": "INFLUENCER-ANNA", "organizationId": "organizations/7-A", "name": "Anna's channel",
  "utmSource": "youtube", "commissionBasisPoints": 1000 }
```

The storefront calls `trackFromUrl(location.href)` (or `POST …/track { ref, utmSource, … }`) on landing. The visit is
attributed to the visitor (X1) or the account, **first touch** within `AttributionWindow` (30 days), and stored **only
with consent** to `ConsentPurpose` (`analytics`) when `RequireConsent` (the default; startup refuses without the consent
primitive, GA9/X21). A guest's attribution moves to the account at sign-in. The attribution sits beside the basket;
when an order is placed it is copied onto a satellite of the order (`PromotionOrderAttributions/{orderId}`), so
Checkout's order is untouched.

On `Confirmed` the link's commission (percentage of the attributed goods, plus a fixed amount) is booked once into
Payments' platform-fee ledger (GB4) through `ISparkPlatformFeeLedger`: a `Commission` entry
`PlatformFeeEntries/Commission/commerce-promotions/{orderId}` carrying the seller, the promoter (`BeneficiaryId`) and the
link (`AttributionRef`), saved in the same session as its audit entry `commerce-promotions.commission`. Refunds reverse
it in proportion to the amount refunded, a lost dispute in full, as `CommissionReversal` entries
(`…/{orderId}/reversal/{reference}`, never more than was booked). Commissions are not platform revenue: the monthly fee
invoice ignores them. `GET …/admin/links/report?ref=` answers orders placed, confirmed and cancelled, sales and
commissions (net of reversals) per currency, and orders per UTM source; the seller's staff and the link's promoter may
read it. The ledger rows are readable in the `PlatformFeeLedger` query by finance under its row rule.

## 6. Configuration (`Spark:Commerce:Promotions`)

`ManagerRole`, `UsageReservation`, `ReleaseUseOnRefund`, `MaxAttempts`, `BlocksSchedule`, `WaitingList`
(`OfferValidity`, `MaxQuantityPerEntry`, `OfferPagePath`, `OfferMailTemplate`), `BackInStock` (`Enabled`,
`BatchFactor`, `MaxBatch`, `SubscriptionLifetime`, `ConfirmationWindow`, `RequireMarketingConsent`, `Schedule`, paths
and templates), `SalePrices` (`ApplyInPricingStep`, `Schedule`), `Tracking` (`Enabled`, `AttributionWindow`,
`RequireConsent`, `ConsentPurpose`). Startup refuses non-positive windows, factors and counts.

Read-only back-office grids: `PromotionCodes`, `PromotionSalePrices`, `PromotionWaitingLists`, `PromotionTrackingLinks`
(only `QueryRead` is granted; rows narrowed to the caller's sellers).

## 7. Client (`@mintplayer/ng-spark/commerce/promotions`)

`SparkCommercePromotionsService` (every endpoint, `trackFromUrl`, `blockedItemId`, `promotionRefusalOf`),
`<spark-sale-price [itemId]>` and `<spark-back-in-stock [itemId] [signedIn]>`.

## 8. Tests

`tests/MintPlayer.Spark.Commerce.Tests/Promotions`:

- `PromotionRulesTests` (pure): code normalization and windows, eligibility, discounts and the pro-rata spread, per-unit
  split, progressive series, the announced percentage, the activation rule, the back-in-stock batch, attribution,
  commissions, request validation.
- `PromotionFlowTests` (RavenDB with the real inventory engine): blocks (move out, close, refunds after the close, all
  or nothing), exact usage limits and once per buyer, the code step's reservation at placement, the sale step, the
  waiting list's activation, strict order and lapsed offers, holds blocking activation, back-in-stock batches and erasure.
