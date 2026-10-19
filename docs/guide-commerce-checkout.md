# Commerce: checkout

`MintPlayer.Spark.Commerce.Checkout` (alias `commerce-checkout`) turns a basket into a paid order. The design is in
`docs/commerce_PRD.md` §4.2 (baskets), §4.4 (the order lifecycle) and §4.20 (legal documents). This guide explains how
to use it.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();            // payment webhooks
    spark.AddMolliePayments();       // or AddStripePayments(), AddTestPayments()
    spark.AddCommerceCheckout();     // adds Inventory and Payments
    spark.AddTestInsurance();        // optional, MintPlayer.Spark.Commerce.Checkout.Test (GC4, demos only)
});
```

## 1. Telling checkout what you sell: capacity sources

Checkout knows no products and no ticket types. Each one reaches it through an `ICheckoutCapacitySource`: `Handles(itemId)`
claims the item, and `ResolveAsync` answers a `CheckoutItem` with these fields:

- the channel, the seller (`OrganizationId`), the price, the price basis and the VAT rate;
- the title and description, which are the main characteristics shown at the last step;
- `MinimumAge`, `IsPhysicalGoods`, `ServiceDate`, the per-buyer limit and its scope;
- `AdmissionScope`, the waiting-room gate;
- the add-on parents (`RequiresParentItemIds`);
- `Capacity`, the claim shape: items through their quota mappings, quotas, seats, best-available chains and picks.

Shop and Ticketing.Events register theirs. A host registers its own for anything else: it is discovered with
`[SparkDiscoverable]`.

## 2. Baskets

There is one basket per account and one per guest visitor (X1). The guest's basket moves onto the account at sign-in,
inside the visitor merge's cluster-wide transaction. Baskets are written only in cluster-wide sessions (S18), so they
are not persistent-object types. The endpoints read and write them:

| Endpoint | Does |
|---|---|
| `GET /spark/commerce/checkout/basket` | the basket, priced (FinalPrice per line), with the earliest hold deadline |
| `POST …/basket/add` | `{ itemId, quantity, units?, options?, parentLineId?, admissionToken? }` |
| `POST …/basket/quantity` | `{ lineId, quantity }`; 0 removes the line and its add-ons |
| `POST …/basket/choices` | donation, insurance and promotion choices, and a guest's e-mail address |

Each line keeps its own channel's rules (D40). Each line gets **its own inventory claim**, `claims/checkout/{lineId}`:

- in a `HoldFromSelection` channel, the units are held when the line is added;
- in `HoldFromCheckout`, they are held at "Pay";
- in `OnPayment`, they are claimed only when the payment is confirmed (D1).

**Purity and mixing.** A basket holds one channel unless `Spark:Commerce:Basket:MixChannels=true`.

In `ConnectedSellers` mode a basket holds one seller, unless both `Basket:MixSellers` and
`Basket:AcknowledgeSplitMerchantOfRecord` are set (D41). The order is then paid with one split payment
(`PaymentCreation.Splits`, one share per seller). The platform is the merchant of record for that order, and the
payment methods are limited to those that can split. In `Platform` mode the platform is the only merchant, so sellers
mix freely.

**Other rules:**

- **One currency** per basket.
- **D4:** a line whose channel has `RequireSignIn` needs a signed-in buyer.
- **D21, D43:** the seller gate `ISparkSellers.CanSellAsync` is checked when a line is added and again at "Pay".
- **GB2 add-ons** need their parent line, and go with it when the parent is removed or refused.

Refusals answer HTTP 409 with `SparkCheckoutRefusal { code, lineId, subject }`. The client translates
`commerceCheckout.refusal.{code}` (`CheckoutRefusals`).

## 3. The last step and placing the order (GA2)

`GET /spark/commerce/checkout/summary` returns the pre-contract information (Directive 2011/83/EU Art. 6(1) and 8(2)):

- every line's main characteristics and its price broken down: unit price, VAT, mandatory fees and adjustments;
- the extras and the total FinalPrice;
- the sellers (the client shows each seller's trader label, GA5);
- the legal documents in force, each with `mustAccept`;
- the minimum age, the offerable methods and the payment window;
- the donation and insurance offers;
- the pay button's key, `commerceCheckout.orderWithObligationToPay` ("Order with obligation to pay").

`POST /spark/commerce/checkout/place` checks, in order:

1. **The required documents** (`RequiredAcceptance`, default `terms-of-sale`). They must be accepted **by id**. A newer
   version published after the step was shown answers `termsChanged`. A seller without one answers `termsMissing`. The
   accepted ids and versions are stored on the order.
2. **GB12:** `attestedMinimumAge` must be at least the basket's highest `MinimumAge`. The attestation is stored, and
   every line of such an item gets `IdCheckRequired`, which Tickets and the pickup slip show. No ID document is stored.
3. **The method.** Only methods every held line allows are accepted (D3, D40). A held basket offers only methods that
   fit the shortest `MaxPaymentWindow` among its held lines, and the channel's `ExcludedMethods` are removed. No method
   is ever filtered by country (GA11).
4. **The claims.** Each line is claimed according to its channel's mode. The claim is extended to the PaymentPending
   deadline: the method's expiry plus `PaymentGrace`, capped by the window.
5. **The payment.** Checkout creates the payment and redirects to the provider. A **zero total** confirms at once,
   without any provider (D6).
6. **GA9 soft opt-in.** When the buyer ticks the checkbox (`marketingOptIn`), it is recorded as a consent record with
   `ISparkConsent.RecordAsync` (purpose `marketing-mail`, source `checkout`). ⚠️ The ePrivacy Art. 13(2) conditions
   still have to be confirmed against EUR-Lex (D37).

The **confirmation** is mailed through MailManager (`SparkCheckout/OrderConfirmation`). This mail is the durable medium
of CRD Art. 8(7). It carries the accepted documents in its body, title and text in the buyer's language, plus PDF
links. ⚠️ MailManager has no attachments yet, so the PDFs are linked rather than attached (Q-O9).

## 4. The order state machine

```
PaymentPending ─▶ Confirmed | Cancelled | Expired | Voided | Refunding
Expired/Cancelled ─▶ Confirmed (late payment re-claimed) | Refunding
Confirmed ─▶ PartiallyRefunded | Refunding | Refunded | Disputed
Disputed ─▶ DisputeWon | DisputeLost
```

`OrderStateMachine` refuses every other move, so a late webhook can never move a refunded order back to Confirmed.
Checkout learns what the provider did through its `IPaymentEventRecipient` (the webhook queue) and a sweep that runs
every 30 seconds. The sweep reconciles PaymentPending orders whose deadline has passed: it confirms them if the
provider says they are paid, and otherwise expires them and cancels the provider payment actively.

- **Confirmation.** Held lines are converted. OnPayment lines are claimed and converted together.
- **Late payment (D3).** A hold that lapsed re-claims the **same** units; seats chosen by chain fill come back as
  explicit seats. If the units are gone, the line is refused.
- **Refused lines.** A refused line follows `Basket:OnLineFailure` (D40):
  - `RefundLine` (default): the rest is confirmed and the line is refunded alone. The donation stays, because GC3
    refunds it only with the whole order.
  - `VoidOrder`: everything is released, and the payment is voided or refunded in full.
  - If no line at all can be sold, the whole payment is voided or refunded.
- **Card fingerprint (D29).** The per-buyer limit is checked again under the card's fingerprint, as a late limit at
  conversion. Over the limit, the order is voided (authorized) or refunded (Mollie reports the fingerprint after
  payment).
  - For Stripe-like methods that know the fingerprint before confirmation and support authorization, checkout uses
    authorize-then-capture whenever a line has a limit. The card is therefore never charged when it is refused.
  - `Channels:{name}:AuthorizeThenCapture` turns this on for a whole channel.
- **A second payment** for an order that is already paid is refunded (or voided).
- **The buyer cancels** (`POST …/order/cancel-payment`). The provider payment is cancelled actively. A
  HoldFromSelection hold that is still alive goes back to the basket; everything else is released.
- **Disputes (GB1).**
  - **Open:** the order becomes `Disputed`, and Tickets freezes its tickets on the event.
  - **Won:** `DisputeWon`.
  - **Lost:** `DisputeLost`. Every line whose service date has not passed is returned once, keyed by
    `{disputeId}:{lineId}` (I4). Shipped goods return no quota until restock.
  - The order's evidence section (`checkout.order`) goes into the payments evidence pack: purchase IP, visitor,
    accepted terms with versions, age attestation and the decision log.

Shipping and Fulfilment call `ISparkCheckout.MarkShippedAsync(orderId, lineId, at)`, and
`ISparkCheckoutRefunds.MarkDeliveredAsync` on delivery.

Refunds, cancellations, the withdrawal function (GA1), guarantee claims (GA8) and mass refunds are covered in
[guide-commerce-refunds.md](guide-commerce-refunds.md).

## 5. Extension points (PRD §4.1 coupling rule)

| Interface | Who registers it | Called |
|---|---|---|
| `ICheckoutCapacitySource` | Shop, Ticketing.Events, the host | when an item is added and at "Pay" |
| `ICheckoutPricingStep` (`Order`) | Promotions (~100); checkout's donation (900) and insurance (950) steps | every quote, side-effect free |
| `IOrderEventRecipient` | Tickets, Invoicing, Fulfilment, Promotions | after each stored change (`Placed`, `Confirmed`, `LineRefused`, `Refunded`, `Disputed`, …); must be idempotent |
| `ICheckoutAdmissionGate` | the waiting room (M16) | when a line with an `AdmissionScope` is claimed; `Consume` marks the claim that uses the admission |

**The admission gate is a seam.** Checkout defines `ICheckoutAdmissionGate` and the waiting room implements it. Checkout
does not reference the waiting room. While no gate is registered, a gated item is refused (`admissionRequired`):
checkout fails closed.

## 6. Back-office orders (GB7)

Back-office orders are made by members of the seller who hold the `box-office` role (`BackOfficeRole`), or its Owners
and Admins, or the same people of the platform organization.

| Endpoint | Does |
|---|---|
| `POST …/box-office/orders` | `origin`: `phone` (a payment link is mailed), `manual` (marked paid later), `comp` or `guestList`. Comp and guest-list orders have a zero total, count against quotas and per-buyer limits, and are flagged `ExcludedFromRevenue` |
| `POST …/box-office/mark-paid` | the `finance` role only. Audited (`commerce-checkout.order-marked-paid`) with the payment reference |
| `POST …/box-office/resend` | re-sends the confirmation. Tickets re-sends its tickets on `ConfirmationResent` |
| `POST …/box-office/change-line` | a seat change or an upgrade, in three idempotent steps: claim the new units, return the old ones, convert. A lower price is refunded; a higher one is charged through a supplement payment link |

⚠️ PRD §4.4 asks for the seat change to happen in one cluster-wide transaction. The inventory engine has no exchange
operation, so it is done in steps instead. A failure leaves at most a hold that lapses on its own; it never causes a
double sale.

## 7. Abandoned baskets, donations, insurance

- **GC1: the abandoned-basket reminder.** It is opt-in per channel (`Channels:{name}:AbandonedBasketReminder`). One
  mail goes out after `AbandonedBaskets:Delay`, and only when all of these hold:
  - the consent primitive is wired;
  - the buyer granted `marketing-mail` (account preference or the e-mail's soft opt-in);
  - **no line holds seats**: a hold expiring is never a trigger.

  The mail carries the consent unsubscribe link and MailManager's one-click `List-Unsubscribe`. ⚠️ The consent
  primitive's own `List-Unsubscribe` headers cannot be passed through MailManager yet.
- **GC3: donations.** Configure `Donation:Enabled`, `Beneficiary`, `FixedAmounts` per currency and `RoundUp`. The
  buyer's choice is `donation=5` or `donation=roundup`. VAT category `outside-scope` (configurable).
- **GC4: insurance.** An `IInsuranceProvider` quotes after the main price. The offer is never pre-ticked: the buyer
  chooses `insurance=accept`. The cover is bound at confirmation. ⚠️ The UCPD and CRD Art. 22 rules on pre-ticked
  boxes are still to be confirmed against EUR-Lex (D37).

## 8. Geo-blocking (GA11)

No checkout input carries a country, nationality, residence or card-issuing country, and a test guards this. No rule in
checkout can therefore refuse a buyer, or treat one differently, on those grounds (Regulation (EU) 2018/302 Art. 3–5).
⚠️ The wording is still to be confirmed against EUR-Lex (D37). Delivery zones belong to Shipping.

## 9. Configuration (`Spark:Commerce:Checkout`)

`PaymentGrace`, `ConfirmationHold`, `RequiredAcceptance`, `LinkedDocuments`, `Channels:{name}` (`ExcludedMethods`,
`AuthorizeThenCapture`, `AbandonedBasketReminder`), `OrderPagePath`, the mail templates, `BackOfficeRole`,
`MarkPaidRole`, `SweepSchedule`, `AbandonedBaskets`, `Donation`, `Insurance`. Startup refuses:

- an unknown channel name in this section;
- an insurance provider that is not registered;
- a donation with nothing to offer.

## 10. Tests

All tests are in `tests/MintPlayer.Spark.Commerce.Tests/Checkout`:

- `CheckoutRulesTests` (pure): the state machine, per-order fees counted once, donations, the merge rule, the reminder
  rule, the evidence section and the GA11 structural guard.
- `CheckoutFlowTests` (RavenDB with the real inventory engine and fake payments):
  - every mode, purity and mixing, D41 splits;
  - terms, age, zero total, admission fail-closed, add-ons;
  - race losers under both policies;
  - late payments (re-claimed and refunded), card fingerprint, a second payment;
  - cancel, disputes, donation and insurance, the visitor merge.
