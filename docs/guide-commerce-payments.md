# Commerce payments — what you write

`MintPlayer.Spark.Commerce.Payments` (alias `commerce-payments`) and its adapters `.Mollie`, `.Stripe`, `.Test`
(commerce PRD §4.5, milestone M6). Checkout decides *when* to pay and what a late payment means; this package talks to
the provider, records what it did under our references, and tells the rest of the suite.

## 1. Register a provider

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();          // payment webhooks are handled from the spark-webhooks queue
    spark.AddMolliePayments();     // or AddStripePayments(), or AddTestPayments() for E2E and demos
});
```

Credentials live in user-secrets under `Spark:Commerce:Payments:Mollie` / `:Stripe` (see each adapter's README) and
are read per call, never logged. Several providers at once need `Spark:Commerce:Payments:Provider`.

| Setting (`Spark:Commerce:Payments`) | Meaning |
|---|---|
| `Provider` | the default provider |
| `PlatformFee` | `PercentBasisPoints` + `Fixed` per currency: the application fee in `ConnectedSellers` mode (D5) |
| `PlatformOrganizationId` | whose Owners, Admins and `finance` members see every seller's disputes, fees and reconciliation |
| `Methods:{provider}:{id}` | per-method overrides (`Enabled`, `MaxExpiry`, `SupportsSplit`) |
| `PublicBaseUrl` | base of webhook and redirect URLs (else `Spark:Auth:PublicBaseUrl`) |
| `Connect` | `StateLifetime`, `ReturnPath`, `KycRefreshSchedule` |
| `Disputes` | `ChargebackFinalAfter` (120 days), `NotifyRole` (`finance`), `MailTemplate`, `UrlFormat` |
| `Settlements` | `ImportSchedule` (hourly), `IncludeConnectedAccounts`, `FirstImportLookback` |

## 2. Pay

```csharp
var methods = payments.GetOfferableMethods(new PaymentMethodQuery { Held = true, MaxPaymentWindow = channel.MaxPaymentWindow });
var payment = await payments.CreatePaymentAsync(new PaymentCreation
{
    Reference = order.Id!, SellerOrganizationId = order.SellerId, Amount = order.Total,
    Description = "Order " + order.Number, AllowedMethods = methods.Select(m => m.Id).ToList(),
    ExpiresAt = hold.PaymentPendingUntil, RedirectUrl = "/orders/" + order.Number,
});
return Results.Redirect(payment.CheckoutUrl!);
```

- **Idempotent.** The same `Reference` and `Attempt` returns the same payment; raise `Attempt` only to make a new one
  on purpose. Provider calls carry derived `Idempotency-Key`s (`PaymentIdempotencyKeys`).
- **D3, the expiry cap.** In a held channel `GetOfferableMethods` keeps only methods whose provider expiry fits the
  payment window, or that can be expired on demand and confirm at once. `PaymentMethodCatalog.PaymentPendingWindow`
  gives the hold's extension (method expiry + grace, capped by the window). Cancel actively when the hold dies:
  `CancelAsync(paymentId)`.
- **Authorize-then-capture:** `Capture = PaymentCaptureMode.Manual`, then `CaptureAsync` or `VoidAsync`.
- **GA11.** Nothing in this API takes a country: no method is hidden and no payment refused for the card's issuing
  country, residence or nationality (Regulation (EU) 2018/302 Art. 5). A test guards it structurally. ⚠️ The EUR-Lex text
  could not be fetched when this was written; the article's wording is quoted from the research and must be confirmed
  (D37).

## 3. Hear what happened

Implement `IPaymentEventRecipient` (discovered: `[SparkDiscoverable]`). Every method is called after the change is
stored, from the webhook queue, and **must be idempotent** (a throw retries the delivery):

| Method | When |
|---|---|
| `OnPaymentChangedAsync` | paid, authorized, pending, expired, failed, cancelled (never back from paid; a late payment after expiry still lands) |
| `OnRefundChangedAsync` | a refund changed; `FailureReason = InsufficientBalance` when a seller's balance cannot cover it (D20) |
| `OnDisputeChangedAsync` | a dispute opened, changed, won or lost (GB1). Lost: release with the dispute id as the I4 key |
| `OnSellerAccountChangedAsync` | a seller's KYC state changed (D21) |

## 4. Refund

`RefundAsync(paymentId, amount, reference)` is idempotent per `reference`. In `ConnectedSellers` mode the refund comes
from the seller's own balance; when it cannot, the refund is **recorded as failed, never thrown**, a `RefundFailed`
item lands in the reconciliation queue, and the recipients hear it. Retry later with `attempt: 1`. The platform fee's
pro-rata share is given back and booked in the fee ledger.

## 5. Sellers (`Spark:Commerce:Merchant = ConnectedSellers`)

An Owner or Admin of a seller starts `POST /spark/commerce/payments/connect/start` (`<spark-seller-payment-account>` in
`@mintplayer/ng-spark/commerce/payments` does it), finishes the provider's onboarding and returns to
`/spark/commerce/payments/connect/callback`. Mollie: OAuth (tokens stored encrypted with Data Protection, renewed with
the refresh token; register the callback URL with the Mollie app). Stripe: a connected account and an Account Link.
The KYC state reaches Commerce's seller gate through `ISellerKycSource`; nobody can be charged on an account that is
not verified and able to take charges.

## 6. Disputes (GB1)

A dispute or chargeback becomes a `PaymentDispute`; the seller's Owners, Admins and `finance` members are told
(`IPaymentNotifier`, or a MailManager mail `SparkPayments/DisputeOpened`). In connected mode the provider's debit and its
fee are booked in the fee ledger, and reversed when the dispute is won.

The **evidence pack** is assembled from every `IDisputeEvidenceContributor` (Checkout, Tickets, Shipping and Commerce
register theirs): `GET /spark/commerce/payments/disputes/evidence?id=…` downloads it; the `PaymentDisputeSubmitEvidence`
action submits it where the provider takes evidence by API (Stripe). Mollie takes evidence only in its dashboard and
gives chargebacks no decision: `PaymentDisputeAccept` closes one as lost, and one still open after
`Disputes:ChargebackFinalAfter` is closed as lost by a nightly job. The 120-day default is the owner's decision (D42,
2026-10-19), matching the card schemes' dispute windows; Mollie's public docs state no deadline. Configurable.

## 7. Settlements and the fee ledger (GB4)

Every adapter's `ISettlementSource` is imported hourly, for the platform account and each linked seller. Each line is
matched to a payment (amount equal), refund, dispute or ledger fee; unmatched lines and amount differences become
`ReconciliationItem`s in the `ReconciliationQueue` query for `finance`, resolved with a note
(`PaymentReconciliationResolve`). The `PlatformFeeLedger` query lists every application fee, fee refund, dispute debit,
dispute fee and reversal per seller, append-only.

## 8. The Stripe held-flow shape (spike S5, decided)

Checkout Sessions (hosted, redirect: the same flow as Mollie, no Stripe.js) with `expires_at` = the hold's PaymentPending
deadline clamped to Stripe's 30 min – 24 h, **plus** an active `POST /v1/checkout/sessions/{id}/expire` when the hold
dies. Immediate methods are therefore "expire on demand" in the catalog; SEPA Direct Debit and bank transfer are
"delayed" and never offered in held channels. A payment already in flight when the session is expired can still
complete: Checkout's late-payment path re-claims or refunds (research §4.4). PaymentIntents with the Payment Element were
the alternative; rejected because they need Stripe.js on the host's pages (GA9 consent) and a second buyer flow.

## 9. Split payments (D40, D41)

`PaymentCreation.Splits` splits one payment across sellers. Mollie: routing (`Mollie:SplitPayments`, needs Mollie to
enable split payments on the account). Stripe: separate charges and transfers (`Stripe:SeparateChargesAndTransfers`),
the transfers made once the payment is paid.

Both route the money through the platform's own account, so for a split order the **platform is the merchant of
record**, the one exception to D5 ("the platform never holds third-party funds"). The owner allowed it as an opt-in
(D41, 2026-10-19): a split payment is created only when

- `Spark:Commerce:Merchant` is `ConnectedSellers`,
- `Spark:Commerce:Basket:MixSellers` **and** `Spark:Commerce:Basket:AcknowledgeSplitMerchantOfRecord` are on, and
- the chosen provider (and method) can split.

Otherwise `CreatePaymentAsync` refuses with `commercePayments.splitNotEnabled` (no opt-in),
`commercePayments.splitNeedsConnectedSellers` or `commercePayments.splitUnsupported`. Commerce's startup validation
refuses `MixSellers` without the opt-in or when no registered provider can split. Off by default; read the licensing
and KYC warning in the commerce guide ("Mixed-seller baskets") before switching it on.

## 10. Testing

`AddTestPayments()` gives a payment page (`/spark/commerce/payments/test/pay?id=…`) and `TestPaymentSimulator`:
success, failure, pending, expiry, authorization, late and duplicate webhooks, refunds that succeed, stay pending or fail
for lack of balance, disputes won and lost, Connect onboarding and settlements. Its webhooks take the real route.
Adapter tests use **doc-derived fixtures** (`tests/MintPlayer.Spark.Commerce.Tests/Payments/Fixtures`, round 4) until the
sandbox accounts exist; no test calls a provider.
