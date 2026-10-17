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

---

### M0 — Spikes

- [ ] S0 demo-app feasibility (both systems, one App_Data)
- [ ] S18 Community feature checklist (written first: it constrains S1–S3)
- [ ] S1 engine correctness (0 oversell, I1–I5, sweeper killed)
- [ ] S2 engine throughput (≥150 holds/s, p95 ≤250 ms, shards K ∈ {1, 8, 32, 64}, chain cursor)
- [ ] S3 sweeper and live availability (≤5 s)
- [ ] S4 waiting room at 200k on cloud VMs (owner provisions and destroys)
- [ ] S5 Mollie + Stripe sandboxes; real fixtures recorded
- [ ] S6 Connect onboarding (both providers), including the insufficient-balance refund
- [ ] S7 seat-map rendering (50k seats)
- [ ] S8 PBKDF2 cost (server ≤10 ms; WebCrypto ≤300 ms on low-end Android)
- [ ] S9 offline scanner list and double-entry sync
- [ ] S10 `ISaveReplacement` proposal round trip
- [ ] S11 name-collision analyzer (SPARK057–062, code fixes)
- [ ] S12 bot defence S1–S10 (research §6)
- [ ] S13 carrier sandboxes (Sendcloud, bpost, PostNL)
- [ ] S14 Peppol sandboxes (two access points, after Q-O2)
- [ ] S15 mass refund at scale with crash and resume
- [ ] S16 visitor identity merge and anonymous-POST antiforgery
- [ ] S17 live broadcast topics + private per-identity stream
- [ ] S19 server-side meta tags validated by SeoTester's OpenGraph checks
- [ ] S20 fulfilment round trip over the WD1 transport with a stub consumer (shared with the warehouse transport spikes)
- [ ] S21 rounding: pro-rata discounts, partial refunds, VAT reconciliation
- [ ] S22 production-migration dry runs (D26 join requests, D28 names → ids)
- [ ] S24 `TranslatedString` usage inventory (libs, apps, indexes, renderers, stored documents incl. production)
- [ ] S23 zone-name fallbacks (.NET ICU on Linux; Intl longGeneric per browser and language)

### M1 — Framework additions and core defects

- [ ] X1 visitor identity (`ISparkVisitor`, Data Protection cookie, merge on sign-in, antiforgery on anonymous POSTs)
- [ ] X2 live broadcast topics, server time in frames, private per-identity stream, identity-keyed socket caps
- [ ] X3 atomic natural-id create, with takeover of expired `IExpiring` documents (F6)
- [ ] X4 `Money` value type + model attribute type + ng-spark renderer + index companions
- [ ] X5 `MintPlayer.Spark.Webhooks`; GitHub webhooks re-pointed onto it
- [ ] X6 server-side meta tags for SPA routes
- [ ] X7 `ISaveReplacement` + on-behalf-of seam (F4: MCP "Applied" fix)
- [ ] X8 Organizations publishable OR mode + custom roles (`scanner`, `finance`)
- [ ] X10 rate limiter: identity partitions + IPv6 /64 grouping (F5)
- [ ] X11 `SparkUser.CreatedAtUtc`
- [ ] X12 generic claims primitive + cluster-wide multi-key unit of work
- [ ] X13 phone verification on `MintPlayer.Spark.Sms` (+ adapters per Q-O1, + test adapter)
- [ ] X16 date-time display facility: model rule `dateTimeDisplay` (viewer | stored | zone | zoneFrom + hint); ng-spark `<spark-date-time [value] [alsoIn] [mode]>` component + pipe (`Intl.DateTimeFormat`; second rendering auto-hidden when the wall clocks match at that instant; translated IANA city label) (D35); generic grids and detail pages use it; .NET `ISparkDateTimeFormatter` twin; fold in the identity provider's private date pipe (D34)
- [ ] D36 `ISparkTimeZoneNames` (server + client) with fallback chain: reference-data provider (registered by the reference-data client package, M19) → platform-localized generic name → IANA-derived city; cache per (zone, language); non-blocking render; S23 decides step 2 per platform
- [ ] K10 enforcement: `BannedSymbols.txt` for non-core projects through the existing `BannedApiAnalyzers` wiring (`Directory.Build.targets:235-241`); a `tools/verify-*.test.mjs` client guard with a violating fixture that proves it fires; fix every existing violation it finds
- [ ] X17 one name system: redesign `TranslatedString` (entries: value + RFC 5646 tag + optional kind/validity/source/romanization; RFC 4647 fallback server + client; any language; sanitized index field names; display rule); migrate every usage found by S24, production data included
- [ ] X18 fuzzy-marked fields/search rules → `SearchEngineType.Lucene` set automatically on that index; deterministic side-by-side rebuild on an engine change; analyzer for fuzzy queries against unmarked indexes
- [ ] X19 library-declared index definitions (incl. maps contributed to a shared multi-map index) emitted into the app by the generator at build time; commerce, warehouse and reference-data libraries ship declarations, never index classes
- [ ] X20 source-generator programme: (1) D28 names constants (effective names + library ids) or delete unused; (2) C#→TS contract tool (layout per the pending output-path investigation; CI diff check; typed records replace anonymous objects for ng-spark responses; migrate existing TS mirrors, fixing the `PersistentObjectAttribute.options` and `EntityPermissions` drifts); (3) one shared discovery generator (+ row policies, `IReplicaChanged<T>`, approval deciders/handlers, account hooks); (4) `GenerateIndexGenerator`: Money/Quantity companions, sanitized language fields, fuzzy marker, record fix; (5) enum-backed lookup generator; (6) GS1 tables tool (C# + TS); (7) runtime ETL from contracts; (8) public plugin helpers; (9) guards: translation-key test (fix `dashboards.yes`, `common.unexpectedError`), K10 banned symbols, migration Version from class name, authorization endpoint filter
- [ ] X15 po-form edit renderer for a single AsDetail value object (`spark-po-form.component.html:381-398`)
- [ ] X14 recipient locale: time zone + culture on the account (preference) and captured onto orders at checkout; `ISparkRecipientLocale` used by mails, PDFs and background jobs (D33)
- [ ] F10 duplicate actions fail at startup; F11 same-name custom actions fail at startup
- [ ] F2 moderation decide race (one session, red test first)

### M2 — Persistent-object name identity (X9, D28)

- [ ] `PersistentObjectName<T>(name)` + non-generic `(alias, declaredName, name)`; shared `SparkRenames` applied at layer load (runtime `SparkLayerCatalog`, build `LibraryLayersReader`)
- [ ] Renames rewrite the model name, queries' `entityType`, right targets and translation keys; provenance hashes on raw JSON; the collection follows the final name
- [ ] Explicit deltas `"$extends": "alias:Name"`; the synchronizer matches by clrType and writes `$extends` (fix F7; fix the F8 message); QnA `SparkUser.json` + SeoTester `Organization.json` updated
- [ ] Orphaned or stale deltas refused (F9)
- [ ] Actions resolved by `IPersistentObjectActions<T>` contract; CLR-less types use `{DeclaredName}Actions` in their declaring assembly
- [ ] Analyzer SPARK057–062 with code fixes (symbol-location reporting)
- [ ] Rename handling: `names` map in `modelHashes.json`, the synchronizer migrates app files, a type-identity column in `securityPosture.txt`
- [ ] Stored names → ids (moderation `TargetType`, `AgentGrant.Types`, dashboard ids, share-link keys) + migration (S22)
- [ ] Fix CLR-name == PO-name assumptions (`SparkShareOptions.cs:36`, `ContributionDescriptor.cs:43,81`, `OrganizationActions.cs:47`) and string self-lookups (`PasskeysActions.cs:43`, `TeamActions.cs:38`)

### M3 — Approvals (D9, D26)

- [ ] Decision-request engine (`ApprovalSubject`, `IApprovalDeciders<T>`, `IApprovalHandler<T>`, `ISparkApprovals`, `ApprovalRequest` PO, notifications, mail, audit, expiry, live, write guard)
- [ ] Document proposals on X7 (config per type Create/Modify; apply as approver with `Etag = base`; stale → 409; pending creations invisible; generic diff + `diffComponent` slot; move `diffRevision` into models)
- [ ] `@mintplayer/ng-spark/approvals` (detail panels, actions, title badge, po-edit / po-create hooks, grid type hints)
- [ ] Migrate the IdP developer request, scope approval (F1 red first) and go-live review; delete the replaced files (≈ 20 files); rewrite `OidcApprovalTests.cs`; record reject reasons (F3)
- [ ] Migrate the Organizations join request (in-app approve by owner/admin, decline, requester notified) + production migration + old-link redirect
- [ ] Regenerate gates in SparkId, CodeCoverage, SeoTester, Fleet

### M4 — Commerce core

- [ ] `MintPlayer.Spark.Commerce.Abstractions` + `MintPlayer.Spark.Commerce` (alias `commerce`)
- [ ] Channels, bounds and clamping; fee rules; price display incl. mandatory fees
- [ ] Seller profile on Organizations; `Sellers:Registration`; Approvals wiring; KYC gate hook
- [ ] Startup validation (refusals and warnings, PRD §4.2); config-wins precedence
- [ ] Audit trail on money-moving changes; GDPR hooks

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

### M7 — Checkout

- [ ] Baskets (visitor or account), channel-pure
- [ ] Order state machine (PRD §4.4) for all three modes; zero-total orders
- [ ] PaymentPending extension capped by `MaxPaymentWindow`; method filtering in held channels
- [ ] Late payment: re-claim the same units, else refund; OnPayment race loser: void or refund
- [ ] Admission-token check for waiting-room-gated phases
- [ ] Linked-identifier limits; card-fingerprint enforcement (Stripe pre-charge refuse; Mollie ≤60 s void/refund)
- [ ] Extension points: pricing steps, order-event recipients, capacity sources

### M8 — Promotions + waiting list

- [ ] Codes (percentage, fixed, unlock, capacity-blocking quota), limits, windows, once-per-buyer, presale access
- [ ] Pro-rata discount allocation (feeds refunds and VAT)
- [ ] Waiting list with personal time-limited codes; activation rule (sold out + queue closed + no holds)

### M9 — Refunds and cancellations

- [ ] Refund policies within channel bounds; fee refund rules
- [ ] Mass refund (batched, resumable, idempotent, progress, rate limits, insufficient-balance handling)
- [ ] Reschedule keep-or-refund window; 14-day withdrawal for goods
- [ ] Every refund: credit note, exactly-once counter returns, ticket revocation

### M10 — Invoicing

- [ ] Gap-free numbering per issuer per year; receipts, invoices, credit notes (PDF)
- [ ] Host-defined VAT categories and rates; VIES check; issuer per merchant mode; platform-fee invoices
- [ ] E-invoice abstraction (EN 16931 / Peppol BIS UBL) + two access-point adapters (Q-O2) + test

### M11 — Shipping + carriers + fulfilment

- [ ] Methods and zones, free threshold, pickup, `IShippingRateProvider`
- [ ] `.Sendcloud`, `.Bpost`, `.PostNL`, `.Test` (labels, tracking webhooks via X5)
- [ ] `MintPlayer.Spark.Commerce.Fulfilment` (shop side) over `MintPlayer.Spark.Fulfilment.Contracts`, using the WD1 transport (ETL when licensed, else SyncAction → owner → HTTPS push + digest reconcile; module client certificates); state both ways, one owner per fact (WD2); stub consumer test

### M12 — Shop

- [ ] Products, variants (own quota), categories, search
- [ ] Order states Paid → Shipped → Delivered; returns; 14-day withdrawal
- [ ] Shop storefront pages (M17 completes the shell)

### M13 — Ticketing events

- [ ] Event → Dates → Sale phases; ticket types; prices per category; per-sale policies within bounds
- [ ] One-screen defaults for a one-off event; multi-date phase → one room, availability per date
- [ ] PreSelectOrder (ranked alternatives; auto-hold at admission; presence check; one-click release)

### M14 — Venues and layouts

- [ ] Shared venues (address from reference data, M19), owned layouts, copy-to-edit, Approvals for modify (always) and create (configurable)
- [ ] Snapshots per published date
- [ ] Editor: polygons over background, seat generation, labelling, standing, decor, chain order, seat tags, price categories, CSV import/export, undo/redo, snapping, touch, keyboard, canvas level of detail
- [ ] Buyer seat picker + accessible list alternative; accessible seating rules

### M15 — Tickets and scanner

- [ ] Issuance (PBKDF2-hashed 128-bit secrets, QR = id + secret), delivery lead time, ticket page + PDF mail
- [ ] Named tickets, transfer, face-value resale with reissue; freeze before doors
- [ ] Scanner PWA: event-scoped device token, online atomic check-in, offline hashed list, sync with double-entry flags, revocation, wipe after the event

### M16 — Waiting room and bot defence

- [ ] Rooms, schedules, RandomizedPreQueue (recorded seed), Ballot (commit-reveal seed, staged single-use links), Fifo (`@last-modified`)
- [ ] One entry per identity; dense ranks via a single ranker; public broadcast topic + private admission stream
- [ ] Admission worker under a lease (max active, rate, projected capacity), presence check, signed single-use tokens
- [ ] Truthful sold-out handling
- [ ] `MintPlayer.Spark.Challenges` (ALTCHA built in) + `.Turnstile`, `.HCaptcha`, `.Recaptcha`, `.FriendlyCaptcha`; runtime-switchable points; 449 step-up
- [ ] Risk pipeline (`ISparkRiskSignal`), deterministic-only blocking, decision log
- [ ] Deceptive measures: each a separate switch, off by default, deterministic triggers by default, startup warning, logged for appeal

### M17 — Storefront + meta tags

- [ ] Routed, replaceable pages for shop, events, waiting room, basket, checkout, orders, tickets; theme variables; translations; WCAG
- [ ] `provideSparkCommerce(...)`, `withShopRoutes()`, `withTicketingRoutes()`, `withWaitingRoomRoutes()`
- [ ] Server-side meta tags wired for public event and product routes

### M18 — Demo app

- [ ] `apps/<Demo>` with shop + ticket channels (ConnectedSellers, test provider, test carrier); https 5015 / http 5016
- [ ] New-app checklist (workspaces, slnx, pull-request.yml build + verify loops, verify-ngsw-config, test-local E2E apps, regenerate.yml, test.yml matrix)
- [ ] E2E suite (PRD §14 flows); seed data without personal data

### M19 — Reference-data app

- [ ] Per `docs/reference_data_PRD.md` (to be written after its research and grilling)
- [ ] Commerce `Address` + typeahead integration with a host-configurable endpoint

### M20 — Warehouse plugin

- [ ] Per `docs/warehouse_picking_PRD.md` (to be written after its research and grilling)
- [ ] Replication defects F12–F19, red test first (F19: ETL deploy endpoint lets the caller choose TargetDatabase/TargetUrls — suspected security) (refused write-back recorded as Completed; non-durable dispatch; retry budget; stale overwrite; ETL script reads beyond its grant; attribute rights in module sync; ETL task cleanup and licence check)
- [ ] Cross-app transport per the warehouse PRD (push + reconcile proposed)

### M21 — Documentation

- [ ] A guide per package (`docs/guide-commerce-*.md`, `guide-approvals.md`, `guide-waiting-room.md`, `guide-challenges.md`, …) + READMEs
- [ ] Update `guide-library-layers.md` for `$extends` and renames; `guide-live-updates.md` for topics

### M22 — Sweep, verification, release

- [ ] `npm run test:affected` with the Developer licence (raw log), fix and re-run single failing classes only
- [ ] Browser verification through the Playwright MCP tools (storefront, editor, waiting room, scanner)
- [ ] Version bumps (preview numbers; ng-spark minor)
- [ ] VPS checklist (PRD §8) prepared for the owner's deploy, starting with periodic RavenDB backups and a tested restore
