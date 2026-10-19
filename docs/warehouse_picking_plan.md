# Warehouse order picking (#521, D13): plan

This file is the order of work for [warehouse_picking_PRD.md](warehouse_picking_PRD.md).
- **Decisions** live in the PRD (§13); this file carries the checklists.
- **Evidence** lives in [warehouse_picking_research.md](warehouse_picking_research.md).
- **Scope:** these milestones are the content of commerce M20 ([commerce_plan.md](commerce_plan.md)) and land in the same PR.

## Rules for executing this plan

- **One PR** for everything: commerce, the reference-data app (K8), the warehouse plugin (D13), and every defect found along the way (commerce §6, including F12–F19).
- **Commit per milestone. Run no tests per milestone.**
  - Verify by reading code and type-checking.
  - The test sweep runs once, in WM13, inside commerce M22.
- **Logs go to the scratchpad raw** (`cmd > log 2>&1; echo EXIT: $?`), then are filtered. Never pipe the only copy.
- **Versions:**
  - bump the preview number of every touched `libs/` csproj;
  - new packages start at `11.0.0-preview.1`;
  - `@mintplayer/ng-spark` gets one minor bump for the whole PR;
  - majors never change (repo rule).
- **No backward compatibility** (K3): change core behaviour directly. Replication changes in place, and Fleet and HR are rewired without shims.
- **No string-spliced RQL.**
  - Values go through parameters, identifiers through `RqlIdentifier`.
  - SKUs, location codes and scanned barcodes are untrusted input.
- **Spikes record measured results in PRD §13** (numbers, file:line, red→green) before the milestone that depends on them starts.
- **Load spikes:**
  - run only in windows the owner chooses (commerce D24);
  - use RavenDB pinned to 3 cores, with no feature Community lacks;
  - no CPU burners while the owner works.
- **Device spikes** run on hardware the owner provides (Q-W21).
- **Licence realism.** Local runs and CI use the Developer licence, which has ETL and no index caps. So:
  - replication tests pin `Transport=Http` and run again with `Etl` (Q-W24);
  - index counts are asserted, never assumed (W19).
- **Ordering against commerce:**
  - WM1 lands before commerce M11's fulfilment work;
  - WM9's `scanning` and `offline` entry points land before commerce M15's scanner PWA.
- **Secrets.** The owner sets these in user-secrets; never print or paste them:
  - carrier sandbox keys;
  - the QZ Tray signing certificate;
  - print-agent credentials.
- **Short folder names** under `libs/` (D31). Aliases are permanent.
- **No personal data** in fixtures or seed data.
- **Angular component styles** go in a sibling `.scss` (`styleUrl`), never inline.

---

### WM0 — Spikes

Commerce S18's Community checklist gains the warehouse items (PRD §7). W19 and that checklist come first, because they constrain the index design.

**Stock and processes**
- [ ] W1 stock engine invariants: 0 WI1–WI6 violations over ≥100 runs × 10k ops; p95 pick-confirm ≤150 ms; retry rates on hot balances and `StockLevel` recorded
- [ ] W2 stock feed without oversell: 0 in ≥1,000 single-channel runs; the multi-channel bound recorded; red→green on refused cancellation and on refund of shipped units; ≤10 s p95 through the real HTTP transport
- [ ] W3 release, allocation and batching: 1,000 orders × 3 lines over 5,000 locations, with FEFO, in ≤30 s; 50-line route ≤50 ms
- [ ] W4 zone split and consolidation: 0 units lost or misplaced over ≥100 runs; no incomplete order released; no double-assigned slot

**Replication**
- [ ] W5 HTTP transport failure modes (consumer down, producer down, crash after commit, crash before 200, duplicates, reordering, poison, raw writes, exhausted budget): converges within one reconcile, 0 lost, 0 duplicated
- [ ] W6 ETL semantics on Developer, and WX13 licence detection on Developer and Community
- [ ] W7 F12–F19 red, then green; write-path refusal reaches the initiator
- [ ] W8 contract genericity: a Spark app without commerce packages; no `Commerce.Checkout` reference (covers commerce S20)

**Devices and scanning**
- [ ] W9 keyboard wedge: 100% correct over 1,000 scans per device; ≤150 ms p95; GS handled
- [ ] W10 AZERTY detection: 100% of 500 garbled scans flagged; 0 false flags on 500 correct scans and 200 typed entries
- [ ] W11 camera: ≥95% decoded within 1.5 s; 0 external requests; bundle impact recorded
- [ ] W12 GS1 corpus: ≥500 cases, C# = TypeScript = Syntax Engine
- [ ] W13 offline replay: exactly once; conflicts as exceptions; survives a restart; `persist()` granted; wipe on sign-out
- [ ] W14 live pick queue with 50 handhelds: costs recorded; assignment ≤1 s p95; 0 deliveries to the wrong user

**Printing and sign-in**
- [ ] W15 label printing paths: ≤3 s p95 via agent and bridges; printed codes scan back
- [ ] W16 agent exactly-once: one label per job id over 1,000 jobs with injected faults; PDF fallback within 10 s
- [ ] W17 PDF library, shared with commerce Q-O9; the result logged in both PRDs
- [ ] W18 shift sign-in: hybrid passkey sign-in succeeds ≥19/20 with median ≤20 s; idle sign-out within 5 s of the timeout

**Licence limits**
- [ ] W19 index limits: read `/license/status` first (Community caps lifted 2024-05-22, #18564); budget checks (≤12 static, ≥2 left for the host, refusal behaviour, WX13 warning fixture) only if a cap still applies; always: 0 auto indexes from library pages

### WM1 — Replication redesign (WX1–WX4, WX13; F12–F19)

All paths below are under `libs/replication/MintPlayer.Spark.Replication/` unless stated otherwise.

**Transport and contracts**
- [ ] WX13 `ISparkLicenseCapabilities`: read the server's licence at startup (ETL; subscription and index limits). The API is recorded by W6
- [ ] Transport abstraction: `Spark:Replication:Transport` = `Auto` | `Http` | `Etl`
  - `Etl` without an ETL licence refuses startup with an actionable message (F18)
  - today it fails with a generic 500 (`Endpoints/EtlDeploy.cs:113-115`; `Services/EtlTaskManager.cs:92`)
- [ ] Contracts (WX2):
  - producer side: `Publish<TSource, TContract>(name, version, map)`
  - consumer side: replica types bound to `(contract, versions)`, with sources from `Spark:Replication:Consume:{contract}:Sources`
  - grants: `Replicate/{contract}` (today `Replicate/{Collection}`, `Endpoints/EtlDeploy.cs:97-111`)
  - retire `[Replicated]` without a shim (`libs/replication/MintPlayer.Spark.Replication.Abstractions/ReplicatedAttribute.cs:9,16,34`)
- [ ] Consumer registration with each source over mTLS, replacing the ETL deployment broadcast (`Extensions/SparkReplicationExtensions.cs:84,95-96,135-139`)
  - the owner keeps a subscription only while its grant holds
  - add the removal path for subscriptions and ETL tasks (F18; `Services/EtlTaskManager.cs` has none)
- [ ] F19, red test first: push URLs and ETL destinations come from the registry entry of the authenticated module, never from the request. Today the request decides (`Endpoints/EtlDeploy.cs:97-111`; `Extensions/SparkReplicationExtensions.cs:135-147`; `Services/EtlTaskManager.cs:52-60`)

**HTTP path**
- [ ] Capture:
  - an `IBeforeSave` stamps `ReplicationVersion`
  - an `IAfterSaveCommitted` / `IAfterDeleteCommitted` writes the outbox entry in the save's commit (`libs/spark/MintPlayer.Spark.Abstractions/Interceptors/SparkCommittedInterceptors.cs:3-20`)
  - `IReplicaPublisher.MarkChanged(session, id)` covers raw writes
- [ ] Delivery:
  - one messaging queue per consuming module; the handler loads the current state
  - `POST /spark/replicas/apply`
  - 5xx or unreachable → `RetryAfterException` (`libs/messaging/MintPlayer.Spark.Messaging.Abstractions/RetryAfterException.cs:3-14`)
  - 4xx → dead-letter with its reason, plus an alert
  - retry budget per queue (`libs/messaging/MintPlayer.Spark.Messaging/Services/MessageBus.cs:122`)
- [ ] Apply endpoint:
  - version guard and tombstones
  - source-module check (a foreign id is poison)
  - read-only replicas (no `New` or `Delete` rights)
  - a durable `IReplicaChanged<T>` (previous and new state) in the same session
- [ ] Digest endpoint `GET /spark/replicas/{contract}/digest?after=&take=`, plus the consumer reconcile cron (`libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:239-273`) and a run at startup. The initial load is a full reconcile
- [ ] A dirty-set flush helper for high-churn contracts; the outbox refuses `DeduplicationKey` (`libs/messaging/MintPlayer.Spark.Messaging/IMessageOutbox.cs:13-18`)

**ETL path**
- [ ] Owner-generated transforms from contracts (Q-W17)
- [ ] One task per consumer per owner (`Services/EtlTaskManager.cs:25-26`)
- [ ] Consumer scripts are never deployed (F16; today they go out verbatim, `Services/EtlTaskManager.cs:65-70`)

**Write path**
- [ ] A replica edit becomes a SyncAction request, stored in the edit's own session through commerce X7 `ISaveReplacement`; no local save first (F13; `Services/ReplicationInterceptor.cs:16`)
- [ ] F12: per-action outcomes, and the initiator is notified (`Endpoints/SyncApply.cs:149-161,171-173`; `Workers/SyncActionSubscriptionWorker.cs:75-85`)
- [ ] F14: a configurable budget with backoff, and re-drive of Failed requests
  - `libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/RetryNumerator.cs:40,46,117-120`
  - `Workers/SyncActionSubscriptionWorker.cs:20`
  - `Services/SyncActionRetrySweeper.cs:77-79,85-89`
- [ ] F15: per-document ordering and op-id idempotency at the owner (`libs/spark/MintPlayer.Spark/Services/SyncActionHandler.cs:43-54`)
- [ ] F17: attribute rights for module principals (`SyncActionHandler.cs:132`; `docs/guide-authorization.md:122`)
- [ ] F18: nested values keep their JSON structure (`Services/SyncActionInterceptor.cs:170-185`, today `:184`)
- [ ] The SyncAction worker and `Indexes/SparkSyncActions_ByStatus.cs` register only when a replica type allows write-back (today unconditional, `Extensions/SparkReplicationExtensions.cs:49`)

**Security, demos, tests**
- [ ] WX4 operator-managed read-only registry mode
  - registration: `Services/ModuleRegistrationService.cs:33-35,58-68`
  - today a registration failure stops setup: `Extensions/SparkReplicationExtensions.cs:84`
  - the fields at risk: `libs/replication/MintPlayer.Spark.Replication.Abstractions/Models/ModuleInformation.cs:18,30`
- [ ] Rewire Fleet and HR:
  - contracts `cars`, `people`, `companies`
  - grants: `apps/Fleet/Fleet/App_Data/security.json:107-112` and `apps/HR/HR/App_Data/security.json`
  - replica types: `apps/HR/HR.Library/Replicated/Car.cs`, `apps/Fleet/Fleet.Library/Replicated/Person.cs`, `Company.cs`
  - wiring: `apps/Fleet/Fleet/Program.cs:20-47,100-104`, `apps/HR/HR/Program.cs`, and both `appsettings.json` files
  - regenerate `modelHashes.json`, `securityPosture.txt` and route snapshots (`apps/Fleet/Fleet.Tests/RouteTableSnapshotTests.cs`, `apps/HR/HR.Tests/RouteTableSnapshotTests.cs`)
- [ ] Red tests for F12–F19 (W7). The 207 precedent is `tests/MintPlayer.Spark.E2E.Tests/Security/CrossModuleSyncTests.cs:116-134`

### WM2 — Shared foundations

- [ ] `MintPlayer.Spark.Fulfilment.Contracts` (`libs/fulfilment/contracts/`): v1 of `fulfilment-orders`, `catalog-items`, `expected-returns`, `fulfilment-status`, `shipments`, `stock-levels` and `return-receipts` (PRD §4.3); `expected-receipts` too if Q-W9 is accepted
- [ ] `MintPlayer.Spark.Gs1` (`libs/gs1/`):
  - check digits (GTIN, SSCC, location)
  - SSCC allocation (company prefix, extension digit, compare-exchange sequence)
  - AI element strings and Digital Link parsing from GS1's AI dataset, kept as data (Q-W16)
  - the W12 corpus as tests
- [ ] `MintPlayer.Spark.Warehouse.Abstractions` (`libs/warehouse/abstractions/`): `IAllocationRule`, `IPutawayRule`, `IPickStrategy`, `IPickRouter`, `ICartonizer`, `IBarcodeResolutionStep`, `ISiteRouter`
- [ ] WX8 `Quantity`, on X4's value-type infrastructure (model types stop at `decimal`, `libs/spark/MintPlayer.Spark.Abstractions/Model/SparkModelShape.cs:191-216`)
- [ ] WX9 hierarchical reference picker: `loadChildren` (flat today, `libs/node_packages/ng-spark/po-form/src/reference-query-tree-select-provider.ts:35-38`), plus materialized-path helpers
- [ ] WX11 generic CSV import and export, shared with the commerce seat CSV (D32)
- [ ] WX14 zone-aware schedules in core cron, with DST gaps and folds defined (Q-W1). Today it is UTC only (`libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:76,101-102,209`; `CronScheduleParser.cs:21`)
- [ ] WX16 composable multi-map indexes, if Q-W26 is accepted

### WM3 — Warehouse core

**Package and master data**
- [ ] `libs/warehouse/core/`, alias `warehouse`: `spark.AddWarehouse()` binds `Spark:Warehouse`, with startup validation as in PRD §4.13
- [ ] Sites (IANA zone; address from reference data) and zones
- [ ] Locations: separate documents, materialized path, walk sequence, check digits, types, geometry fields. Deactivated, never deleted

**Owners and items**
- [ ] Owner links (Q-W23)
- [ ] `IOrganizationOwned` on every owner-bearing document (`libs/organizations/MintPlayer.Spark.Organizations/IOrganizationOwned.cs:25-29`)
- [ ] Explicit owner filters in every library query; raw session queries are unfiltered (`IOrganizationOwned.cs:21-22`)
- [ ] Items:
  - consume `catalog-items`
  - `WarehouseItem` extensions, with (owner, SKU) as a unique key
  - warehouse-owned items
  - pre-items and linking (Q-W12)
  - tombstoned replicas keep their stock
- [ ] Barcode registry, with `barcodes/{code}/…` ids for prefix lookups, plus the resolution pipeline (`IBarcodeResolutionStep`)
  - several matches → the user chooses
  - unknown → the link-to-item right

**Stock engine**
- [ ] Documents: `StockLot` (`DateOnly` expiry, `SparkModelShape.cs:206`), `StockBalance` per (location, item, lot), `StockMovement` keyed by op id
- [ ] Moves and adjustments as custom actions in one session, with OCC retry (`libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:46-77`)
- [ ] Cluster-wide mode (`libs/authorization/MintPlayer.Spark.Authorization/Identity/UserStore.cs:203-209`)
- [ ] Atomic first-put-away create (commerce X3)
- [ ] Reason codes; stock read-only in the UI
- [ ] `StockLevel` per (owner, item), updated in the movement's session (WI6)
- [ ] `stock-levels` publication: dirty-set flush; per-channel projection filtered by owner links
- [ ] Min/max replenishment: daily sweep plus every short pick; fixed pick faces only; lots chosen by FEFO
- [ ] Round 5 (commerce GA7): Recalled lots blocked at once (allocations re-allocated or short-picked); owner-scoped, audited **recall trace** report lot → movements → shipment lines → fulfilment orders (+ stock still on hand), CSV export; the shop resolves buyers and sends the notice; item + date-range trace for untracked items
- [ ] Round 5 (commerce GC9): dangerous-goods data on `WarehouseItem` (UN number, class, packing group, lithium-battery kind, limited quantity); zone mixing rules can segregate them

**Work, indexes, guard**
- [ ] Task envelope: `WarehouseTask` (one collection for every kind) and `WarehouseTaskGroup`; assignment, leases (commerce X12), op ids
- [ ] Productivity events
- [ ] Static indexes per the PRD §4.5 budget: `Warehouse_Stock`, `Warehouse_LedgerTotals`, `Warehouse_Work`, `Warehouse_Catalog`, `Warehouse_Documents`
  - deployed through `AddIndexesFrom` (precedent: `libs/moderation/MintPlayer.Spark.Moderation/SparkModerationExtensions.cs:79`)
  - every library query bound to one of them
- [ ] A scaled-down concurrency guard for WI1–WI6, kept in the suite (it runs in WM13)

### WM4 — Inbound

- [ ] `libs/warehouse/inbound/`, alias `warehouse-inbound`
- [ ] Expected (ASN) and blind receipts:
  - GS1 lot, expiry and count capture
  - discrepancies
  - receipt sources per Q-W9
- [ ] Put-away:
  - rules in order: fixed → same lot → nearest empty (by walk sequence)
  - custom rules through `IPutawayRule`
  - Putaway tasks confirmed by scanning
- [ ] Returns:
  - consume `expected-returns`
  - receipt (scanned or blind) and inspection
  - disposition: Restock, Quarantine, Scrap or Return to vendor
  - round 5 (commerce GA8): the return reason per line (`Withdrawal`, `NonConformity`, `WrongItem`, `DamagedInTransit`, `SellerAllowed`, `Other`) sets the default disposition; inspection overrides it with a reason code
  - publish `return-receipts`
- [ ] Contribute this package's maps to `Warehouse_Documents` (Q-W26)

### WM5 — Outbound

**Intake and allocation**
- [ ] `libs/warehouse/outbound/`, alias `warehouse-outbound`
- [ ] Intake: `fulfilment-orders` → `OutboundOrder` (`outbound/{fulfilmentOrderId}`); accept or reject, with reasons; site routing (rules, else the owner's default site)
- [ ] Release:
  - immediate, by waves (cut-offs per site, WX14), or manual
  - the shop's holds respected; `ShipComplete` honoured
- [ ] Allocation:
  - FIFO, FEFO or closest, per owner and per item (`IAllocationRule`)
  - candidates from `Warehouse_Stock`; claims made on loaded balances

**Picking**
- [ ] Pick work:
  - discrete, batch, cluster (totes, unique through X12) and zone
  - waves as grouping rules
  - walk-sequence routing (`IPickRouter`)
- [ ] Scan-to-confirm:
  - order: location or check digits → item (expected one pre-selected) → lot check (Q-W6) → quantity → tote
  - batch leases, with supervisor takeover
- [ ] Short picks:
  - zero the remaining balance, with a reason
  - re-allocate
  - backorder per the shop's policy
  - optionally trigger a count and a replenishment
- [ ] Backordered lines re-allocated when stock arrives

**After picking**
- [ ] Cancellation handshake per state (PRD §4.3):
  - `Cancellation:AfterPick` (Q-W2)
  - put-back tasks
  - publish `fulfilment-status`
- [ ] Consolidation:
  - put wall and consolidation station
  - release to packing only when complete, unless partial shipment is allowed
  - the W4 bookkeeping
- [ ] Contribute this package's maps to `Warehouse_Documents` (Q-W26)

### WM6 — Printing

- [ ] `libs/printing/core/`, alias `printing`:
  - printers
  - jobs with idempotent ids and states
  - routing
  - delivery to the PDF print dialog
  - the agent endpoints (pull, ack, heartbeat)
  - the `Printing_Jobs` index
- [ ] WX6 rendering:
  - barcodes: Code 128, GS1-128, EAN-13, DataMatrix, QR. Today only QR exists (`libs/authorization/MintPlayer.Spark.Authorization/Identity/SparkQrCodeRenderer.cs:17-44`, registered by `Extensions/SparkAuthenticationExtensions.cs:91`)
  - ZPL templates (203 and 300 dpi)
  - the PDF seam, with the library chosen by W17 (commerce Q-O9)
  - the label set: locations, items and cases, totes, SSCC logistic labels, packing slips, pick lists
  - golden tests offline; no Labelary in CI
- [ ] `libs/printing/agent/`, the worker:
  - outbound connection to the app; TCP 9100 output
  - local journal; exactly-once acknowledgement
  - scale reading
  - heartbeat and update check
  - credential and distribution per Q-W14
- [ ] `libs/printing/qz-tray/`: the signing endpoint, with the host's certificate from user-secrets or configuration
- [ ] `libs/printing/zebra/`: the adapter; the host supplies the library (Q-W15)
- [ ] PDF fallback when the agent is down; a reprint is a new job

### WM7 — Packing

- [ ] `libs/warehouse/packing/`, alias `warehouse-packing`
- [ ] Pack station flow:
  - scan the tote or order; verify lines
  - cartonise, manually or by volume (`CartonType`)
  - weigh, manually or through the agent
  - capture serials
  - create the label through `Commerce.Shipping`; print the packing slip
  - round 5: only carrier services accepting the parcel's dangerous-goods classes, with label marks (commerce GC9); customs documents for non-EU destinations (GB6); optional return label in the parcel (GB5)
  - allocate an SSCC through `Gs1`
- [ ] Ship-confirm:
  - on hand −n and `ShippedTotal` +n in one session (WI6)
  - `WarehouseShipment` at `{fulfilmentOrderId}/shipments/{n}`, with the lots per line (round 5: feeds the GA7 recall trace)
  - publish `shipments`
  - handover per Q-W10
- [ ] Carriers:
  - an account per owner, or the site's default
  - tracking webhooks (commerce X5)
  - parcels keep reporting to the app that labelled them
  - the label-void capability
- [ ] Contribute this package's maps to `Warehouse_Documents` (Q-W26)

### WM8 — Counting

- [ ] `libs/warehouse/counting/`, alias `warehouse-counting`
- [ ] Count plans: ABC every 30/60/120 days, spread evenly; class assignment per Q-W8; spot counts
- [ ] Blind Count tasks:
  - each covers a whole location, held under a claim while counted (X12)
  - within tolerance → an adjustment; otherwise → supervisor review

### WM9 — Client entry points

- [ ] `libs/node_packages/ng-spark/scanning/` (WX5):
  - wedge capture: `inputmode="none"`, terminator plus timing, manual fallback, symbology identifiers, check digits
  - camera: native `BarcodeDetector`, or the same-shape `barcode-detector` ponyfill (WASM self-hosted, lazy-loaded)
  - GS1 parser (Q-W16)
  - AZERTY detection and `event.code` decoding
  - feedback and Screen Wake Lock
  - the browser-support matrix
- [ ] `libs/node_packages/ng-spark/offline/` (WX7):
  - an IndexedDB command queue (op ids, device time)
  - replay on `online`, on demand, and after successful requests
  - the exception list
  - `navigator.storage.persist()` and the offline warning
  - per-user partitions, wiped on sign-out (Q-W13)
  - keep `tools/verify-ngsw-config.test.mjs:90-96` green: no `/spark` data groups
- [ ] `libs/node_packages/ng-spark/printing/`: clients for the PDF print dialog, QZ Tray and Zebra Browser Print; job delivery through commerce X2's private stream
- [ ] `libs/node_packages/ng-spark/warehouse/` (+ `/inbound`, `/outbound`, `/packing`, `/counting`):
  - handheld task pages, the pack station, supervisor pages, the barcode-choice dialog
  - `provideSparkWarehouse(with…Routes())`, with routes replaceable one by one
  - theme variables, translations, WCAG
- [ ] WX15 idle sign-out or lock in `ng-spark/auth`, plus the server's session settings (Q-W13, Q-W25)
- [ ] BYOD data minimisation: no addresses in cached pick tasks. Hybrid passkey guidance added to `docs/guide-passkeys.md`

### WM10 — Shop side (commerce work, tracked in commerce M11–M12)

- [ ] `Commerce.Fulfilment` works through `Fulfilment.Contracts`:
  - publishes `fulfilment-orders`, `catalog-items` and `expected-returns`
  - consumes `fulfilment-status`, `shipments`, `stock-levels` and `return-receipts`
- [ ] Fulfilment-order ids unique across shops; cancellation as a status; an edit = a cancellation plus a new order
- [ ] Capacity source from `stock-levels` (the PRD §4.4 formula):
  - `sold` lowered only on a confirmed cancellation
  - an epoch baseline at onboarding
  - bundles through quota multipliers
  - the I4 exception
- [ ] Returns gate: refund on the warehouse's receipt by default; the host can switch to refund on request (WD12); round 5 (commerce GA1): for withdrawals, also on the buyer's proof of dispatch, whichever is earlier
- [ ] Round 5 (commerce GA8): `expected-returns` carries the return reason per line
- [ ] Round 5 (commerce GA7): the shop resolves a recall trace's fulfilment orders to buyers and mails the recall notice
- [x] Sync the commerce text with WD1/WD2 (PRD §0): commerce PRD §4.9's transport paragraph, plus commerce plan M11 and S20 *(docs only; S20 synced in commerce round 5, 2026-10-19)*

### WM11 — Demo app and E2E

- [ ] `apps/Warehouse` (`Warehouse`, `Warehouse.Library`, `Warehouse.Tests`):
  - https 5019 / http 5020
  - database `SparkWarehouse`, cookie `.SparkAuth.Warehouse`, module name `Warehouse`
  - `Transport=Http` pinned (Q-W24)
  - test carrier and PDF printing
  - two seeded owners; no personal data
- [ ] New-app checklist:
  - workspaces and `MintPlayer.Spark.slnx`
  - the build and verify loops in `.github/workflows/pull-request.yml`
  - the app list in `tools/verify-ngsw-config.test.mjs:64-69`
  - `E2E_APPS` in `tools/test-local.mjs:33`
  - `regenerate.yml`, route snapshots, `modelHashes.json`, `securityPosture.txt`
  - the `test.yml` matrix
- [ ] Two-host fixture:
  - one shared `SparkModules` database (today one per host, `tests/MintPlayer.Spark.E2E.Tests/_Infrastructure/FleetTestHost.cs:43`)
  - the generalised host (multi-host plan H1, H2, R2: `docs/multi-host-e2e-plan.md:12-26`)
  - polling with point-loads under a failure bound
- [ ] The end-to-end mTLS test (`tests/MintPlayer.Spark.E2E.Tests/Security/CrossModuleSyncTests.cs:24-31`)
- [ ] The E2E flows of PRD §14; the replication E2E under `Http`, then again under `Etl` (Q-W24)

### WM12 — Documentation

- [ ] Rewrite `docs/guide-cross-module-sync.md` (contracts, transports, the write path), fixing `:117,124,243,245,270-272`
- [ ] Update `docs/guide-replication-mtls.md`: the registry mode, and destinations taken from the registry
- [ ] One `docs/guide-warehouse-*.md` per package
- [ ] `docs/guide-printing.md`: adapters, agent install, QZ signing
- [ ] `docs/guide-scanning.md`: the DataWedge profile, the AZERTY setup, the support matrix
- [ ] `docs/guide-offline.md`
- [ ] A README per package
- [ ] The index budget: counts per library, and how a host binds queries to static indexes

### WM13 — Sweep, verification, release (inside commerce M22)

- [ ] `npm run test:affected` with the Developer licence (raw log to the scratchpad); fix and re-run single failing classes only
- [ ] The replication E2E with `Transport=Http` and with `Etl`; the Community results of W5, W6 and W19 recorded
- [ ] Browser verification through the Playwright MCP tools: handheld pages at phone width, the pack station, the barcode dialog, the offline banner
- [ ] Device verification recorded: W9–W11, W15, W16, W18
- [ ] Version bumps: preview numbers; new packages at `11.0.0-preview.1`; one ng-spark minor, shared with commerce
- [ ] PRD §8: confirm no VPS change; list the conditional steps for the first production replica consumer
