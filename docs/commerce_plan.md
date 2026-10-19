# Commerce suite (#521): plan

The order of work for [commerce_PRD.md](commerce_PRD.md). The PRD carries the decisions (§13); this file carries the checklists. Evidence lives in [commerce_research.md](commerce_research.md).

## Rules for executing this plan

- **One PR** for everything: commerce, the reference-data app (K8), the warehouse plugin (D13), every defect found along the way (§6).
- **Commit per milestone. Run no tests per milestone.** Verify by reading code and type-checking; the test sweep runs once, in M22.
- **Logs go to the scratchpad raw** (`cmd > log 2>&1; echo EXIT: $?`), then are filtered. Never pipe the only copy.
- **Versions:** bump the preview number of every touched `libs/` csproj. New packages start at `11.0.0-preview.1`. `@mintplayer/ng-spark` gets a minor bump. Majors never change (repo rule).
- **No backward compatibility** (K3): change core behaviour directly; data in production apps is migrated (§8).
- **No string-spliced RQL.** Values go through parameters, identifiers through `RqlIdentifier`.
- **Spikes record measured results in PRD §13** (numbers, file:line, red→green) before the milestone that depends on them starts.
- **Load spikes** run only in windows the owner chooses (D24). No CPU burners while the owner works.
- **Secrets:** provider, carrier, Peppol and SMS sandbox keys are set by the owner in user-secrets. Never print or paste them.
- **Round 5 (D37): legal rules are checked against EUR-Lex when implemented.** Before a GA item's code is written, read the official article (EUR-Lex, plus the national transposition where a member-state option applies), cite it in the code comment and the guide, and record any rule that could not be confirmed. The gap analysis cited secondary sources only.
- **Round 5 (D37): third-party features use public-documentation fixtures + `.Test` adapters** (round 4 rule, extended to disputes, settlements, service points, customs, insurance, DAC7 XML and TEDB).
- **Round 5 ids:** GA1–GA11, GB1–GB14, GC1–GC11 (PRD §4.21). A milestone line names the ids it delivers.

---

### M0 — Spikes

- [x] S0 demo-app feasibility (both systems, one App_Data)
- [x] S18 Community feature checklist (written first: it constrains S1–S3)
- [x] S1 engine correctness (0 oversell, I1–I5, sweeper killed) — CI size green; the full run is dropped (round 4)
- [~] S2 engine throughput — dropped (owner, round 4: no stress tests); harness kept, env-gated
- [x] S3 sweeper and live availability (≤5 s)
- [~] S4 waiting room at 200k — dropped (owner, round 4: no cloud VMs)
- [ ] S5 Mollie + Stripe: documented-API fixtures (round 4; sandbox recordings later); round 5: dispute/chargeback events (GB1), settlement/payout reports (GB4), split capability (D40)
- [ ] S6 Connect onboarding (both providers), including the insufficient-balance refund
- [ ] S7 seat-map rendering (50k seats)
- [x] S8 PBKDF2 cost (server ≤10 ms; ~~WebCrypto ≤300 ms on low-end Android~~ dropped, round 4)
- [ ] S9 offline scanner list and double-entry sync
- [x] S10 `ISaveReplacement` proposal round trip
- [x] S11 name-collision analyzer (SPARK057–062, code fixes)
- [ ] S12 bot defence S1–S10 (research §6)
- [ ] S13 carriers: documented-API fixtures (round 4) (Sendcloud, bpost, PostNL); round 5: service points, return labels, multi-parcel (GB5), customs documents (GB6), dangerous-goods restrictions (GC9)
- [ ] S14 Peppol: documented-API fixtures + EN 16931 Schematron (round 4) (two access points, after Q-O2); round 5: reverse-charge and export invoices validate (GA4)
- [ ] S15 mass refund at scale with crash and resume
- [x] S16 visitor identity merge and anonymous-POST antiforgery
- [x] S17 live broadcast topics + private per-identity stream
- [x] S19 server-side meta tags validated by SeoTester's OpenGraph checks
- [ ] S20 fulfilment round trip over the WD1 transport with a stub consumer (shared with the warehouse transport spikes)
- [x] S21 rounding: pro-rata discounts, partial refunds, VAT reconciliation
- [ ] S22 production-migration dry runs (D26 join requests, D28 names → ids)
- [x] S24 `TranslatedString` usage inventory (libs, apps, indexes, renderers, stored documents incl. production)
- [x] S23 zone-name fallbacks (.NET ICU on Linux; Intl longGeneric per browser and language)

### M1 — Framework additions and core defects

- [x] X1 visitor identity (`ISparkVisitor`, Data Protection cookie, merge on sign-in, antiforgery on anonymous POSTs)
- [x] X2 live broadcast topics, server time in frames, private per-identity stream, identity-keyed socket caps
- [x] X3 atomic natural-id create, with takeover of expired `IExpiring` documents (F6)
- [x] X4 `Money` value type + model attribute type + ng-spark renderer + index companions
- [x] X5 `MintPlayer.Spark.Webhooks`; GitHub webhooks re-pointed onto it
- [x] X6 server-side meta tags for SPA routes
- [x] X7 `ISaveReplacement` + on-behalf-of seam (F4: MCP "Applied" fix)
- [x] X8 Organizations publishable OR mode + custom roles (`scanner`, `finance`)
- [x] X10 rate limiter: identity partitions + IPv6 /64 grouping (F5)
- [x] X11 `SparkUser.CreatedAtUtc`
- [x] X12 generic claims primitive + cluster-wide multi-key unit of work
- [x] X13 phone verification on `MintPlayer.Spark.Sms` (+ adapters per Q-O1, + test adapter)
- [x] X16 date-time display facility: model rule `dateTimeDisplay` (viewer | stored | zone | zoneFrom + hint); ng-spark `<spark-date-time [value] [alsoIn] [mode]>` component + pipe (`Intl.DateTimeFormat`; second rendering auto-hidden when the wall clocks match at that instant; translated IANA city label) (D35); generic grids and detail pages use it; .NET `ISparkDateTimeFormatter` twin; fold in the identity provider's private date pipe (D34)
- [x] D36 `ISparkTimeZoneNames` (server + client) with fallback chain: reference-data provider (registered by the reference-data client package, M19) → platform-localized generic name → IANA-derived city; cache per (zone, language); non-blocking render; S23 decides step 2 per platform
- [x] K10 enforcement: `BannedSymbols.txt` for non-core projects through the existing `BannedApiAnalyzers` wiring (`Directory.Build.targets:235-241`); a `tools/verify-*.test.mjs` client guard with a violating fixture that proves it fires; fix every existing violation it finds
- [x] X17 one name system: redesign `TranslatedString` (entries: value + RFC 5646 tag + optional kind/validity/source/romanization; RFC 4647 fallback server + client; any language; sanitized index field names; display rule); migrate every usage found by S24, production data included
- [x] X18 fuzzy-marked fields/search rules → `SearchEngineType.Lucene` set automatically on that index; deterministic side-by-side rebuild on an engine change; analyzer for fuzzy queries against unmarked indexes *(opt-in design; revised by D38 below)*
- [x] X18 revised (D38, round 5): **fuzzy by default**: every index with searchable fields on Lucene; typo-tolerant Spark search by default (exact and prefix matches rank first); per-field opt-out in the model search rule and the index-declaration marker (X20 item 4 becomes an opt-out); engine pinned explicitly on every index; side-by-side rebuild of existing search indexes, production apps included; analyzer flags fuzzy queries on opted-out fields; reference-data §4.10 engines updated
- [ ] X21 consent primitive (GA9): purposes, consent records per visitor/account (merge on sign-in, withdrawal), `ISparkConsent`, `@mintplayer/ng-spark/consent` banner + settings page, `RequiresConsent` declarations refused at startup when unwired
- [x] X19 library-declared index definitions (incl. maps contributed to a shared multi-map index) emitted into the app by the generator at build time; commerce, warehouse and reference-data libraries ship declarations, never index classes
- [~] X20 source-generator programme (done except: item 2 remaining mirrors and anonymous responses; item 4 Quantity companions wait for WX8; item 7 rest in M20): (1) D28 names constants (effective names + library ids) or delete unused; (2) C#→TS contract tool (layout per the pending output-path investigation; CI diff check; typed records replace anonymous objects for ng-spark responses; migrate existing TS mirrors, fixing the `PersistentObjectAttribute.options` and `EntityPermissions` drifts); (3) one shared discovery generator (+ row policies, `IReplicaChanged<T>`, approval deciders/handlers, account hooks); (4) `GenerateIndexGenerator`: Money/Quantity companions, sanitized language fields, fuzzy marker, record fix; (5) enum-backed lookup generator; (6) GS1 tables tool (C# + TS); (7) runtime ETL from contracts; (8) public plugin helpers; (9) guards: translation-key test (fix `dashboards.yes`, `common.unexpectedError`), K10 banned symbols, migration Version from class name, authorization endpoint filter
- [x] X15 po-form edit renderer for a single AsDetail value object (`spark-po-form.component.html:381-398`)
- [x] X14 recipient locale: time zone + culture on the account (preference) and captured onto orders at checkout; `ISparkRecipientLocale` used by mails, PDFs and background jobs (D33)
- [x] F10 duplicate actions fail at startup; F11 same-name custom actions fail at startup
- [x] F2 moderation decide race (one session, red test first)

### M2 — Persistent-object name identity (X9, D28)

- [x] `PersistentObjectName<T>(name)` + non-generic `(alias, declaredName, name)`; shared `SparkRenames` applied at layer load (runtime `SparkLayerCatalog`, build `LibraryLayersReader`)
- [x] Renames rewrite the model name, queries' `entityType`, right targets and translation keys; provenance hashes on raw JSON; the collection follows the final name
- [x] Explicit deltas `"$extends": "alias:Name"`; the synchronizer matches by clrType and writes `$extends` (fix F7; fix the F8 message); QnA `SparkUser.json` + SeoTester `Organization.json` updated
- [x] Orphaned or stale deltas refused (F9)
- [x] Actions resolved by `IPersistentObjectActions<T>` contract; CLR-less types use `{DeclaredName}Actions` in their declaring assembly
- [x] Analyzer SPARK057–062 with code fixes (symbol-location reporting)
- [x] Rename handling: `names` map in `modelHashes.json`, the synchronizer migrates app files, a type-identity column in `securityPosture.txt`
- [x] Stored names → ids (moderation `TargetType`, `AgentGrant.Types`, dashboard ids, share-link keys) + migration (S22)
- [x] Fix CLR-name == PO-name assumptions (`SparkShareOptions.cs:36`, `ContributionDescriptor.cs:43,81`, `OrganizationActions.cs:47`) and string self-lookups (`PasskeysActions.cs:43`, `TeamActions.cs:38`)

### M3 — Approvals (D9, D26)

- [x] Decision-request engine (`ApprovalSubject`, `IApprovalDeciders<T>`, `IApprovalHandler<T>`, `ISparkApprovals`, `ApprovalRequest` PO, notifications, mail, audit, expiry, live, write guard)
- [x] Document proposals on X7 (config per type Create/Modify; apply as approver with `Etag = base`; stale → 409; pending creations invisible; generic diff + `diffComponent` slot; move `diffRevision` into models)
- [x] `@mintplayer/ng-spark/approvals` (detail panels, actions, title badge, po-edit / po-create hooks, grid type hints)
- [x] Migrate the IdP developer request, scope approval (F1 red first) and go-live review; delete the replaced files (≈ 20 files); rewrite `OidcApprovalTests.cs`; record reject reasons (F3)
- [x] Migrate the Organizations join request (in-app approve by owner/admin, decline, requester notified) + production migration + old-link redirect
- [x] Regenerate gates in SparkId, CodeCoverage, SeoTester, Fleet

### M4 — Commerce core

- [ ] `MintPlayer.Spark.Commerce.Abstractions` + `MintPlayer.Spark.Commerce` (alias `commerce`)
- [ ] Channels, bounds and clamping; fee rules; price display incl. mandatory fees
- [ ] Seller profile on Organizations; `Sellers:Registration`; Approvals wiring; KYC gate hook
- [ ] Startup validation (refusals and warnings, PRD §4.2); config-wins precedence
- [ ] Audit trail on money-moving changes; GDPR hooks
- [ ] Round 5 — `FinalPrice` defined once (`IFinalPriceCalculator`) and an append-only price history per sellable item (GA3; feeds display, resale cap, Omnibus)
- [ ] Round 5 — `LegalDocument` (kinds, owner platform/seller, language versions, versions + effective date, PDF); "open sale" refused without required documents; startup warning (GA2)
- [ ] Round 5 — trader traceability at seller registration (data, ID document blob with restricted rights, best-effort verification via Approvals, 6-month retention, trader page); `AcknowledgeDsaArt29Exemption` (GA5)
- [ ] Round 5 — trader/non-trader label per listing; generated ranking-parameters page; P2B platform terms + seller complaints as Approvals requests (GA5)
- [ ] Round 5 — DSA notice-and-action on listings, events and reviews through Moderation; statements of reasons; transparency export (GA5)
- [ ] Round 5 — DAC7 seller data (TIN, VAT id, birth date, address, IBAN), reminders, payout block (GA6)
- [ ] Round 5 — controller/processor roles, DPA template accepted at registration, audited seller exports of buyers and attendees (GB14)
- [ ] Round 5 — retention per document type with legal floors (confirm per country); invoices never altered (PRD §4.15)
- [ ] Round 5 — startup validation: geo-blocking-forbidden rules refused (GA11), consent-requiring adapters without X21 refused (GA9), `MixSellers` without split refused (D40), missing accessibility statement warned (GA10)

### M5 — Inventory engine

- [ ] Quotas, shards, item→quota mappings with multipliers
- [ ] Claims: `TryClaim` / `Adjust` / `Extend` / `Convert` / `Release`, idempotent by claim id, cluster-wide transaction, I1–I5
- [ ] Numbered units (compare-exchange seats, takeover after expiry + grace)
- [ ] Chain-fill allocator (holes first, cursor run), pick-your-seat validation (no single empty seat)
- [ ] Per-buyer and linked-identifier limit keys
- [ ] Sweeper (cron, seconds), live availability topics
- [ ] Scaled-down concurrency guard test kept in the regular suite (runs in M22)

### M6 — Payments

- [ ] Provider interface, method catalog (max expiry, authorization, fingerprint timing), idempotency
- [ ] Merchant modes; Connect onboarding (OAuth / connected accounts), KYC status, application fees
- [ ] `.Mollie` (fetch-by-id webhooks), `.Stripe` (signed webhooks; held-flow shape per S5), `.Test` (all simulated outcomes)
- [ ] Recorded real fixtures; no live calls in CI
- [ ] Round 5 — dispute events in the provider interface (Stripe `charge.dispute.*`, Mollie chargebacks), evidence pack, seller notification, Connect debit recorded (GB1)
- [ ] Round 5 — `ISettlementSource` per adapter, reconciliation queue for `finance`, platform-fee ledger (GB4)
- [ ] Round 5 — `supportsSplit` in the method catalog (D40); no method hidden by card-issuing country (GA11)

### M7 — Checkout

- [ ] Baskets (visitor or account), channel-pure *(by default; see the D40 line below)*
- [ ] Round 5 — multi-channel basket (D40, GB2): `Basket:MixChannels`, per-line channel rules, one order + one payment, shortest payment window, method intersection, sign-in if any line needs it, `Basket:OnLineFailure` (`RefundLine` default | `VoidOrder`), `MixSellers` only with split capability
- [ ] Round 5 — last step: pre-contract information, terms acceptance with versions stored on the order, "Order with obligation to pay" button, durable confirmation with attached documents (GA2)
- [ ] Round 5 — `Disputed` / `DisputeWon` / `DisputeLost` states; I4 release keyed by dispute id (GB1)
- [ ] Round 5 — back-office orders: `box-office` role, phone/manual orders (payment link or audited mark-paid), comps and guest lists (zero total, counted against quotas and limits), re-send, seat change and upgrade in one cluster-wide transaction (GB7)
- [ ] Round 5 — `MinimumAge` attestation stored on the order; ID-check marker on tickets and pickup slips (GB12)
- [ ] Round 5 — geo-blocking: same conditions for every member state; no refusal by residence or card country (GA11)
- [ ] Round 5 — abandoned-basket reminder, consent-gated, never for held seats (GC1); donation / round-up pricing step (GC3); `IInsuranceProvider` + `.Test`, never pre-ticked (GC4)
- [ ] Order state machine (PRD §4.4) for all three modes; zero-total orders
- [ ] PaymentPending extension capped by `MaxPaymentWindow`; method filtering in held channels
- [ ] Late payment: re-claim the same units, else refund; OnPayment race loser: void or refund
- [ ] Admission-token check for waiting-room-gated phases
- [ ] Linked-identifier limits; card-fingerprint enforcement (Stripe pre-charge refuse; Mollie ≤60 s void/refund)
- [ ] Extension points: pricing steps, order-event recipients, capacity sources

### M8 — Promotions + waiting list

- [ ] Codes (percentage, fixed, unlock, capacity-blocking quota), limits, windows, once-per-buyer, presale access
- [ ] Pro-rata discount allocation (feeds refunds and VAT)
- [ ] Waiting list with personal time-limited codes; activation rule (sold out + queue closed + no holds) — the **ticket mode**
- [ ] Round 5 — back-in-stock mode for shops: subscriptions per variant, batched mails on restock, expiry, GDPR erasure (GB11)
- [ ] Round 5 — seller sale prices with validity; Omnibus reference price (lowest `FinalPrice` of the previous 30 days) rendered automatically with every announced reduction; perishable exemption per item (GA3; confirm against PID Art. 6a and the Commission guidance)
- [ ] Round 5 — promoter/affiliate tracking links with UTM attribution on basket and order, per-link reports, optional commissions into the fee ledger, consent-aware (GC2)

### M9 — Refunds and cancellations

- [ ] Refund policies within channel bounds; fee refund rules
- [ ] Mass refund (batched, resumable, idempotent, progress, rate limits, insufficient-balance handling)
- [ ] Reschedule keep-or-refund window; 14-day withdrawal for goods (unconditional: see the GA1 line)
- [ ] Every refund: credit note, exactly-once counter returns, ticket revocation
- [ ] Round 5 — withdrawal function (GA1; CRD Art. 11a, confirm wording and date on EUR-Lex): permanent labelled button on the order page (account or signed guest link), confirmation step, durable receipt with date and time; model withdrawal form; staff registration of mailed withdrawals; **no approval step**
- [ ] Round 5 — withdrawal refund: price + standard delivery cost within 14 days; withheld until goods received or proof of dispatch (earlier wins); diminished-value deduction with reason and audit (GA1, CRD Art. 13)
- [ ] Round 5 — legal-guarantee claims (repair, replace, price reduction, refund; seller pays return shipping; period per country) (GA8)
- [ ] Round 5 — return-reason taxonomy (`Withdrawal`, `NonConformity`, `WrongItem`, `DamagedInTransit`, `SellerAllowed`, `Other`) driving flow, refund rules and the warehouse disposition default (GA8)

### M10 — Invoicing

- [ ] Gap-free numbering per issuer per year; receipts, invoices, credit notes (PDF)
- [ ] Host-defined VAT categories and rates; VIES check; issuer per merchant mode; platform-fee invoices
- [ ] E-invoice abstraction (EN 16931 / Peppol BIS UBL) + two access-point adapters (Q-O2) + test
- [ ] Round 5 — `IPlaceOfSupplyRule` per line: B2C distance sales with the €10k threshold tracked per seller, admission where the event takes place, B2B reverse charge with the invoice mention, VAT-territory exception list, exports at 0 % (GA4; confirm against Directive 2006/112/EC)
- [ ] Round 5 — quarterly OSS export per seller (GA4); IOSS variant for non-EU sellers (GC8)
- [ ] Round 5 — DAC7 quarterly aggregation and yearly XML + CSV export; ticket sales flagged until confirmed (GA6)
- [ ] Round 5 — accounting export per seller and period: sales journal, payments journal, UBL batch (GB4)
- [ ] Round 5 — optional TEDB rate suggestions from reference data, never applied automatically (GC10)
- [ ] Round 5 — legal-document PDFs attached to confirmations (GA2); invoice retention floor enforced (PRD §4.8)

### M11 — Shipping + carriers + fulfilment

- [ ] Methods and zones, free threshold, pickup, `IShippingRateProvider`
- [ ] `.Sendcloud`, `.Bpost`, `.PostNL`, `.Test` (labels, tracking webhooks via X5)
- [ ] `MintPlayer.Spark.Commerce.Fulfilment` (shop side) over `MintPlayer.Spark.Fulfilment.Contracts`, using the WD1 transport (ETL when licensed, else SyncAction → owner → HTTPS push + digest reconcile; module client certificates); state both ways, one owner per fact (WD2); stub consumer test
- [ ] Round 5 — `IServicePointProvider` (bpost, PostNL, Sendcloud, Test) and the checkout point picker (GB5)
- [ ] Round 5 — shop-only multi-parcel shipments and buyer return labels (GB5)
- [ ] Round 5 — customs: CN22/CN23 or commercial invoice through adapters; DAP/DDP per destination zone shown at checkout (GB6)
- [ ] Round 5 — dangerous-goods classes restrict carrier services and add label marks (GC9)
- [ ] Round 5 — cut-offs and delivery promises skip holidays from the reference-data holiday calendar via the zone-aware schedule (GC11, WX14)
- [ ] Round 5 — "refund on receipt" also triggered by the buyer's proof of dispatch for withdrawals (GA1)

### M12 — Shop

- [ ] Products, variants (own quota), categories, search
- [ ] Order states Paid → Shipped → Delivered; returns; 14-day withdrawal (~~seller-approved request~~: three flows per return reason, GA1/GA8)
- [ ] Shop storefront pages (M17 completes the shell)
- [ ] Round 5 — unit of measure + net quantity; computed unit price shown beside `FinalPrice` (GA3)
- [ ] Round 5 — `ProductSafety` attribute group (manufacturer, EU responsible person, identifier, picture, per-language warnings); publish refused without it for goods; storefront block (GA7; confirm against Reg. 2023/988 Art. 19)
- [ ] Round 5 — legal-guarantee notice and durability-guarantee field on product pages and confirmations (GA8; ECGT applies 27 Sep 2026, confirm)
- [ ] Round 5 — HS code and country of origin per product/variant (GB6); `MinimumAge` (GB12)
- [ ] Round 5 — galleries with per-variant images (alt text required); facet filters on category and declared attributes, counts from a declared index map (GB13)
- [ ] Round 5 — verified-purchase reviews with the verification statement, moderated through Moderation (GC5)
- [ ] Round 5 — Google Merchant / Meta product feed per seller at a stable URL (GC6)

### M13 — Ticketing events

- [ ] Event → Dates → Sale phases; ticket types; prices per category; per-sale policies within bounds
- [ ] One-screen defaults for a one-off event; multi-date phase → one room, availability per date
- [ ] PreSelectOrder (ranked alternatives; auto-hold at admission; presence check; one-click release)
- [ ] Round 5 — `AddOn` ticket types sold only with a parent ticket in the same order, refunded with it (GB2)
- [ ] Round 5 — attendee questions per ticket type (asked at checkout or later until a deadline), stored per position, seller retention + GDPR erasure, sensitive questions consent-gated and excluded from exports by default (GB3)

### M14 — Venues and layouts

- [ ] Shared venues (address from reference data, M19), owned layouts, copy-to-edit, Approvals for modify (always) and create (configurable)
- [ ] Snapshots per published date
- [ ] Editor: polygons over background, seat generation, labelling, standing, decor, chain order, seat tags, price categories, CSV import/export, undo/redo, snapping, touch, keyboard, canvas level of detail
- [ ] Buyer seat picker + accessible list alternative; accessible seating rules

### M15 — Tickets and scanner

- [ ] Issuance (PBKDF2-hashed 128-bit secrets, QR = id + secret), delivery lead time, ticket page + PDF mail
- [ ] Named tickets, transfer, face-value resale with reissue; freeze before doors
- [ ] Scanner PWA: event-scoped device token, online atomic check-in, offline hashed list, sync with double-entry flags, revocation, wipe after the event
- [ ] Round 5 — check-in lists per gate/day/zone; multi-day and combi passes; re-entry (in/out toggling); VIP zones; list ids in offline lists (GB3)
- [ ] Round 5 — comps, guest lists, re-send, seat change and upgrade from the back office (GB7)
- [ ] Round 5 — dispute freeze ("on hold" at the gate) and revocation on loss (GB1); "ID check" banner for age-restricted types (GB12)
- [ ] Round 5 — transfer/resale erase the previous holder's data from the ticket; seller sees the current holder only (PRD §4.11)
- [ ] Round 5 — per-event ticket-PDF templates with fixed QR and legal blocks (GC7); `.ics` attachment in ticket mails via the core formatter (GB9)

### M16 — Waiting room and bot defence

- [ ] Rooms, schedules, RandomizedPreQueue (recorded seed), Ballot (commit-reveal seed, staged single-use links), Fifo (`@last-modified`)
- [ ] One entry per identity; dense ranks via a single ranker; public broadcast topic + private admission stream
- [ ] Admission worker under a lease (max active, rate, projected capacity), presence check, signed single-use tokens
- [ ] Truthful sold-out handling
- [ ] `MintPlayer.Spark.Challenges` (ALTCHA built in) + `.Turnstile`, `.HCaptcha`, `.Recaptcha`, `.FriendlyCaptcha`; runtime-switchable points; 449 step-up
- [ ] Risk pipeline (`ISparkRiskSignal`), deterministic-only blocking, decision log
- [ ] Deceptive measures: each a separate switch, off by default, deterministic triggers by default, startup warning, logged for appeal
- [ ] Round 5 — third-party challenge adapters declare `RequiresConsent`; refused consent falls back to ALTCHA (GA9)
- [ ] Round 5 — presence check: automatic while visible, ≥20 s single action when hidden, rejoin at the previous rank (GA10, WCAG 2.2.1)
- [ ] Round 5 — risk signals never refuse on country alone (GA11)

### M17 — Storefront + meta tags

- [ ] Routed, replaceable pages for shop, events, waiting room, basket, checkout, orders, tickets; theme variables; translations; WCAG
- [ ] `provideSparkCommerce(...)`, `withShopRoutes()`, `withTicketingRoutes()`, `withWaitingRoomRoutes()`
- [ ] Server-side meta tags wired for public event and product routes
- [ ] Round 5 — X22: `sitemap.xml`, JSON-LD `Event`/`Product`, `hreflang`; validated with SeoTester's checks (GB9)
- [ ] Round 5 — X23 embed widget (web component + iframe), top-level hand-over for holds, sign-in and waiting rooms, `frame-ancestors` allow-list (GB8)
- [ ] Round 5 — customer account area: orders, invoice/credit-note downloads, address book, tickets, withdrawals and claims, consent settings, back-in-stock subscriptions, attach guest orders (GB10)
- [ ] Round 5 — hold timer: text deadline, polite announcements, ≥20 s "More time" up to `Hold:Max`, exception reasoning in the accessibility statement (GA10; confirm against W3C Understanding 2.2.1)
- [ ] Round 5 — accessibility statement page from a template; legal pages (terms, privacy, withdrawal info + model form, trader page); consent banner and settings (GA2, GA5, GA9, GA10)

### M18 — Demo app

- [ ] `apps/<Demo>` with shop + ticket channels (ConnectedSellers, test provider, test carrier); https 5015 / http 5016
- [ ] New-app checklist (workspaces, slnx, pull-request.yml build + verify loops, verify-ngsw-config, test-local E2E apps, regenerate.yml, test.yml matrix)
- [ ] E2E suite (PRD §14 flows); seed data without personal data
- [ ] Round 5 — demo turns on `Basket:MixChannels`; E2E for the round-5 flows of PRD §14 (one-payment festival basket, guest withdrawal, dispute, check-in lists, comps, consent fallback, "More time")
- [ ] Round 5 — axe checks in E2E on storefront, basket, checkout, waiting room, seat-picker list, withdrawal function, consent banner (GA10)

### M19 — Reference-data app

- [ ] Per `docs/reference_data_PRD.md` and `docs/reference_data_plan.md` (R0–R14) ~~(to be written after its research and grilling)~~ (written 2026-10-17); developed on this branch (D39)
- [ ] Round 5 — basic seed set (some countries, regions, places with the merger cases, streets) seeded deterministically in the demo and tests (D39; reference-data plan R11)
- [ ] Commerce `Address` + typeahead integration with a host-configurable endpoint
- [ ] Round 5 — optional sources: TEDB VAT-rate suggestions (GC10), holiday calendars (GC11), VAT-territory list in the country profile (GA4)

### M20 — Warehouse plugin

- [x] Per `docs/warehouse_picking_PRD.md` and `docs/warehouse_picking_plan.md` (WM0–WM13) ~~(to be written after its research and grilling)~~ (written 2026-10-17)
- [x] Round 5 — recall trace lot → shipment → buyer (GA7), return reasons in `expected-returns` (GA8), dangerous-goods flags (GC9): warehouse plan WM3, WM4, WM7
- [x] Replication defects F12–F19, red test first (F19: ETL deploy endpoint lets the caller choose TargetDatabase/TargetUrls — suspected security) (refused write-back recorded as Completed; non-durable dispatch; retry budget; stale overwrite; ETL script reads beyond its grant; attribute rights in module sync; ETL task cleanup and licence check)
- [x] Cross-app transport per the warehouse PRD (push + reconcile proposed)

### M21 — Documentation

- [x] A guide per package (`docs/guide-commerce-*.md`, `guide-approvals.md`, `guide-waiting-room.md`, `guide-challenges.md`, …) + READMEs
- [x] Update `guide-library-layers.md` for `$extends` and renames; `guide-live-updates.md` for topics
- [x] Round 5 — `guide-commerce-compliance.md`: every GA item with the EUR-Lex article it was checked against, member-state options, unconfirmed points, and the host's responsibilities (accessibility statement, environmental claims, legal texts); `guide-consent.md` (X21); `guide-search.md` updated for fuzzy by default (D38)

### M22 — Sweep, verification, release

- [x] `npm run test:affected` with the Developer licence (raw log), fix and re-run single failing classes only
- [x] Browser verification through the Playwright MCP tools (storefront, editor, waiting room, scanner)
- [x] Version bumps (preview numbers; ng-spark minor)
- [x] VPS checklist (PRD §8) prepared for the owner's deploy, starting with periodic RavenDB backups and a tested restore; round 5: free disk checked for the D38 side-by-side search-index rebuilds, and a post-deploy search check per production app
