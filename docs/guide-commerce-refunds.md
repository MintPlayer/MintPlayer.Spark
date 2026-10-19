# Commerce: refunds, cancellations, withdrawal and guarantee claims

Everything after the sale lives in `MintPlayer.Spark.Commerce.Checkout`, folder `Refunds/` (milestone M9 of #521). The
design is in `docs/commerce_PRD.md` §4.7 (D20), I4 (§4.3) and the round 5 items GA1 and GA8. `AddCommerceCheckout()`
registers all of it; there is nothing extra to call.

## 1. The model

Each order gets a **refund ledger** (`CheckoutRefundLedgers/{orderId}`). It holds:

- **the terms fixed at purchase:** the seller's refund policy and whether each line carries the withdrawal right. They
  are captured when the order is placed, so a later policy change never touches a sold order;
- **the components:** each line's price part (order discounts allocated in), its fees, and the extras (delivery,
  donation, insurance). Each one records what was settled, refunded and kept;
- **the refunds, the returns** (withdrawals included) **and the guarantee claims.**

The ledger is always saved **in the same session as the order**, with optimistic concurrency. The order's line
statuses, the ledger and a mass refund's counters therefore change together or not at all.

**The arithmetic (spike S21).** Integer cents, rounded once with `Money.RoundDiv`, spread with `Money.Allocate`.
Returning units uses a **cumulative share**: `share(gross, unitsReturnedAfter, quantity) − settledBefore`. Any sequence
of partial refunds then sums exactly to the line. For example, 3 × 1 unit of a 10.00 line refunds 3.33 + 3.34 + 3.33.

**Every refund:**

1. returns the units exactly once (I4). The inventory return id is `{orderId}:{refundId}:{lineId}`. Shipped goods
   return no quota until restock, and per-buyer counts are named on a partial return;
2. calls `ISparkPayments.RefundAsync` with the reference `{orderId}:refund:{refundId}`. The same reference twice
   answers the first refund;
3. raises `TicketsRevoked` for its non-goods units, for Tickets (M15);
4. raises `RefundIssued` once the provider paid it out, or finance settled it by hand.

## 2. Order events for other packages

| Event | Payload | For |
|---|---|---|
| `RefundIssued` | `OrderEvent.Refund` (`OrderRefundIssued`): per line the quantity, the gross paid back (price part and fees), the deduction, the **gross settled before** and the line as sold (gross, net, VAT, rate, category); the extras likewise | Invoicing: one credit note per refund id. These are the S21 strategy H inputs; Invoicing keeps its own credited-VAT state |
| `TicketsRevoked` | `OrderEvent.Revocation`: lines, quantities, seats | Tickets |
| `WithdrawalRequested` | `Reference` = the return id | Fulfilment (stop an unshipped parcel), reports |
| `ReturnExpected` | `OrderEvent.Return`: the reason, the default disposition, who pays the return shipping, the lines | Shop (return label, GB5) and the warehouse (`expected-returns`) |
| `GuaranteeClaimOpened` / `GuaranteeClaimResolved` | `Reference` = the claim id | Shop, support |

Recipients must be idempotent: the refund sweep re-delivers a `RefundIssued` that was saved but not marked delivered.

## 3. Extension points

- **`IRefundTermsSource`** (Shop, Ticketing.Events, the host): an item's seller policy (`None`, `UntilDaysBefore`,
  `WithFee`) and its withdrawal right or CRD Art. 16 exemption.
  - The policy is clamped to the channel's bounds (`Spark:Commerce:Channels:{name}:Refunds`, D20).
  - When no source answers, the policy is `None` (or the first allowed kind).
  - The default withdrawal right: physical goods carry it; a dated service is exempt under Art. 16(l); other services
    follow `Withdrawal:ServicesWithoutDate`.
- **`IStandardDeliveryCost`** (Shop/Shipping): which order extra is delivery, and what the least expensive standard
  delivery would have cost. Without one, the extras coded `delivery` or `delivery:…` count as delivery, all of it
  standard.
- **`ISparkCheckoutRefunds`:**
  - `MarkDeliveredAsync(orderId, lineId, at)` (Shipping/Fulfilment): the withdrawal and guarantee periods start here;
  - `RegisterGoodsBackAsync(...)` (Shop, the warehouse's receipt event): goods received, or proof of dispatch.

## 4. The withdrawal (GA1)

The withdrawal is **unconditional**: no approval step, and staff can only process it. `<spark-checkout-aftercare>` on
the order page (`<spark-checkout-order>` embeds it) shows a permanent button, **"Withdraw from contract here"**
(`commerceCheckout.withdrawal.button`), while a line still carries a live right. The flow:

1. **The form:** which units; an optional reason; name and e-mail for a guest.
2. **The confirmation step:** `confirm: false` previews. Lines outside the right are shown with the reason
   (`periodEnded`, `exempt:16(l)`, …).
3. **Confirm withdrawal** (`commerceCheckout.withdrawal.confirm`) records it at once. The buyer gets the
   acknowledgement: on screen, and as the mail `SparkCheckout/WithdrawalReceipt` with the **date and time it was
   received**.

Lines outside the right are recorded too, so staff see them, but they are not refunded.

A guest reaches the function from the **signed order link** in the confirmation mail (`withdrawal_link`,
`Refunds:GuestOrderPagePath`, Data Protection). A guest who uses the original browser is recognised by the visitor.

**The period:**

- 14 days, counted from the day after the buyer received the **last** goods of the order (CRD Art. 9(2)(b)(i)). It ends
  at the end of the last day, which moves off a weekend (Reg. 1182/71 Art. 3).
- While goods are undelivered, the right is live (Art. 9(3)).
- Days are counted in `Refunds:TimeZone`.
- **Public holidays** (also Art. 3(4)) move the last day too, but only when a holiday calendar is registered and
  `Refunds:HolidayCountry` (and, for regional holidays, `Refunds:HolidaySubdivision`) is set. **The default is
  weekend-only.** The reference data's calendar (GC11) is one line, and needs the reference-data client's sync of
  `PublicHolidays`; a host can register its own `ICheckoutHolidayCalendar` instead:

  ```csharp
  builder.AddSpark(spark => spark
      .AddCommerceCheckout()
      .UseCheckoutReferenceDataHolidays());   // + "Spark:Commerce:Checkout:Refunds:HolidayCountry": "BE"
  ```

  The guarantee period (Directive 2019/771) is never moved.

**The refund:**

- It covers the price, the fees and the **standard** delivery cost; the surcharge for a dearer method is kept (CRD
  Art. 13(1)–(2)).
- It is paid at once for unshipped units.
- For shipped units it is **withheld until the goods are received or dispatch is proven, whichever comes first**
  (Art. 13(3)). Staff register either one with `POST …/refunds/staff/goods-back`.
- It is due 14 days after the withdrawal (`RefundDueBy`). The sweep warns about overdue ones.
- On receipt, staff may deduct **diminished value** (Art. 14(2)) with a reason. The deduction is audited
  (`commerce-checkout.diminished-value`) and allowed only before the withheld refund is paid.
- A receipt with the `Restock` disposition puts the units back on sale, exactly once (I4).

A partial withdrawal refunds the delivery cost when no goods line is kept (`Withdrawal:PartialDelivery = WhenComplete`),
or in proportion (`ProRata`).

**Staff register a withdrawal** received by mail (the model form, CRD Annex I(B)) or by phone with
`POST …/refunds/staff/withdrawal`, giving its receipt time. The registration is audited.

## 5. Cancellation within the seller's policy (D20)

`POST …/refunds/cancel` (preview, then `confirm`) applies the policy fixed at purchase:

- `None` refuses (`noCancellation`);
- `UntilDaysBefore` refuses after the deadline (`cancellationClosed`);
- `WithFee` keeps its fee from the price share.

Fees are refunded only when the channel's `RefundFees` is on.

## 6. Guarantee claims and return reasons (GA8)

Every return carries a **reason**: `Withdrawal`, `NonConformity`, `WrongItem`, `DamagedInTransit`, `SellerAllowed` or
`Other`. `ReturnReasons.For(reason)` decides:

- the flow;
- whether fees are refunded;
- the delivery rule: standard, full or none;
- who pays the return shipping;
- the default disposition: Restock for a withdrawal, Inspect for a non-conformity, Quarantine for transit damage.

**A guarantee claim** (`POST …/refunds/claim`) is open for physical goods within `Guarantee:PeriodDays` of delivery
(default 730). The buyer may prefer repair or replacement (Art. 13(2)). Staff resolve it with
`POST …/refunds/staff/claim`:

- **repair or replacement:** no money moves. The goods come back at the seller's cost (Art. 14(2)).
- **price reduction or refund (termination):** these need an Art. 13(4) ground (`groundRequired`). A termination refunds
  once the goods are back or dispatch is proven (Art. 16(3)). It includes the full delivery cost and the fees.

`POST …/refunds/staff/refund` covers the other reasons: a wrong item, transit damage, goodwill.

## 7. Mass refunds and reschedules (D20, spike S15)

**Starting one.** `POST …/refunds/staff/mass` (finance) starts the run for an item list, for example every ticket type
of a cancelled event.

**How the cron worker runs it** (`Checkout.MassRefunds`):

- It stores the selection once, in chunks, then walks it with a cursor, one batch per run.
- Each order is one session: its refund, ledger and line statuses, the run's counters and the cursor. A crash before
  the save repeats the provider call with the **same reference**, so nothing is refunded twice.
- The counters are exact: `processed`, `skipped`, `refunds`, and `pending/succeeded/failed/insufficientBalance/manual`
  buckets.
- At most `MassRefunds:MaxRefundsPerSecond` provider calls are made. Any error stops the run, and the next run resumes
  at the same order.

**Controlling it.** `GET …/refunds/staff/mass?id=` shows the progress. `POST …/mass/control` takes `pause`, `resume`,
`cancel` or `retryFailed`.

**When a connected seller's balance is short** (D20), the refund is `Failed / InsufficientBalance`. Payments notifies
finance. After a top-up, finance runs `retryFailed`, or `…/staff/retry` for one refund. The manual fallback is
`…/staff/settle`: finance paid it back outside the provider. It is audited and raises `RefundIssued`.

**A reschedule.** `POST …/refunds/staff/reschedule` opens a **keep-or-refund window** and mails every buyer through the
same worker (`SparkCheckout/RescheduleNotice`). Until the window closes, the buyer answers on the order page; a refund
there returns everything paid for those lines, fees included.

## 8. Configuration (`Spark:Commerce:Checkout:Refunds`)

- `TimeZone`
- `Withdrawal`: `PeriodDays` (at least 14), `RefundWithinDays`, `SellerPaysReturnShipping`, `PartialDelivery`,
  `ServicesWithoutDate`
- `Guarantee`: `PeriodDays`, `ChannelPeriodDays`
- `MassRefunds`: `Schedule`, `BatchSize`, `MaxRefundsPerSecond`, `ChunkSize`
- the three mail templates
- `GuestOrderPagePath`
- `SweepSchedule`

## 9. Legal rules and what is still to confirm (D37)

The rules follow the texts below. **EUR-Lex could not be read from the build environment on 2026-10-19:** the page
came back empty to the fetcher. Every rule therefore carries ⚠️ *confirm against EUR-Lex*.

| Rule | Source |
|---|---|
| Withdrawal function: label, confirmation step, acknowledgement with date and time on a durable medium. Applies from 19 June 2026 | ⚠️ CRD Art. 11a, inserted by Directive (EU) 2023/2673. Confirm the exact wording and the date |
| 14 days from receipt of the (last) goods; live before delivery | CRD Art. 9(2)(b), 9(3) |
| Day counting | Reg. (EEC, Euratom) 1182/71 Art. 3 (CRD recital 41). Public holidays (Art. 3(4)) only with a registered calendar and `Refunds:HolidayCountry`; default weekend-only. ⚠️ Art. 3(4) takes the holidays of the member state where the act is done: confirm per country which one applies (seller's vs buyer's) |
| Refund within 14 days, standard delivery only, withholding until goods back or proof of dispatch | CRD Art. 13(1)–(3) |
| Return cost and diminished value | CRD Art. 14(1)–(2) |
| Exemptions (dated leisure services, …) | CRD Art. 16. ⚠️ An item-level exemption comes from `IRefundTermsSource` |
| Partial withdrawal and delivery cost | ⚠️ Not settled by Art. 13. Confirm against the Commission's CRD guidance and national law; configurable |
| Guarantee period of at least 2 years; remedies hierarchy; free of charge, seller takes back; termination refund after goods back | Directive (EU) 2019/771 Art. 10, 13, 14, 16. ⚠️ Confirm the national period (Art. 10(3)) per member state. Art. 13(5) (no termination for a minor lack of conformity) is staff judgement, not checked |
| The mail body as a durable medium | The receipt is the mail body, with the same content attached as a PDF when Invoicing is installed (`ICheckoutMailAttachments`, Q-O9) |

## 10. Tests

All tests are in `tests/MintPlayer.Spark.Commerce.Tests/Checkout`:

- **`RefundRulesTests`** (pure): cumulative shares, amount and unit refunds mixed, discount allocation, fees and
  cancellation fees, the standard delivery rule and the pro-rata variant, deductions, policy bounds, cancellation
  windows, period counting, the last-delivery start, the taxonomy, partial inventory returns, provider references.
- **`RefundFlowTests`** (RavenDB with the real inventory engine):
  - the withdrawal, withheld until proof of dispatch, then restocked once;
  - out of period, and a dated-event exemption;
  - a mailed withdrawal with diminished value;
  - a policy cancellation with a fee, the policy fixed at purchase;
  - a guarantee termination;
  - **S15:** 12 orders, a crash after the 7th provider call, resume. No double refund, exact counters, one failure for
    insufficient balance retried after a top-up, one `RefundIssued` per refund, every unit back once.
