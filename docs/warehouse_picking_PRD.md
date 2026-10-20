# Warehouse order picking (#521, D13): PRD

**Status:** research and grilling round 1 complete (2026-10-17). Spikes not started.
**Issue:** [#521 "Ticket sale platform"](https://github.com/MintPlayer/MintPlayer.Spark/issues/521), commerce decisions D12 and D13.
**Companions:**
- [warehouse_picking_research.md](warehouse_picking_research.md): the evidence, with file:line and sources (§1 WMS domain, §2 hardware and browser, §3 Spark fit, §4 replication defects, §5 transport options, §6 shop ↔ warehouse semantics, §7 proposed scope and framework additions, §8 spikes, §9 sources).
- [warehouse_picking_plan.md](warehouse_picking_plan.md): the order of work.
- [commerce_PRD.md](commerce_PRD.md): the sibling PRD. This PRD:
  - decides what commerce left to it: the transport and the contract package (commerce §4.9);
  - implements commerce's replication defects F12–F19;
  - supplies the scanning and offline stack that the commerce scanner PWA shares (commerce §4.11);
  - is delivered as commerce milestone M20.
- `reference_data_PRD.md`: the reference-data app (commerce K8). Its consumer copies travel over the replication transport decided here (WD1).

> The decision log (§13) is the record. Where the body and the log disagree, the log wins; fix the body.
>
> **Numbering:**
> - owner decisions: WD1–WD18 (WD13–WD18: replication, round 3);
> - spikes: W1–W19;
> - milestones: WM0–WM13;
> - framework additions: WX1–WX16;
> - open questions: Q-W1–Q-W29 (all decided; Q-W27–Q-W29 on 2026-10-20);
> - warehouse invariants: WI1–WI6.
>
> Commerce numbers (D, F, I, K, M, Q-O, S, X) keep their commerce meaning, and so do the commerce round-5 gap items **GA/GB/GC** (commerce §4.21). Three land here: GA7 (recall trace), GA8 (return reasons) and GC9 (dangerous goods).

---

## 0. Status

| Item | State | Date | Record |
|---|---|---|---|
| Research: WMS domain, existing systems, hardware and browser, shop ↔ warehouse patterns | ✅ | 2026-10-17 | research §1, §2, §6 |
| Research: Spark fit, replication defects, transport options | ✅ | 2026-10-17 | research §3–§5 |
| Grilling round 1 (WQ1–WQ12) | ✅ | 2026-10-17 | §13 Round 1 |
| Research verification pass: F19 (the caller chooses the ETL destination); Community index limits (12 static, 24 auto per database; **later found lifted on 2024-05-22**, #18564) | ✅ | 2026-10-17 | §6; §4.5 index budget (corrected); W19 |
| Commerce PRD adjusted for this plugin: `Commerce.Shipping` without Checkout, `Fulfilment.Contracts`, the I4 exception, the stock-feed formula, Q-O9, Q-O5 closed | ✅ | 2026-10-17 | commerce §4.1, §4.3, §4.9, §11 |
| Commerce text still to sync with WD1/WD2 (three places) | ✅ (~~⏳~~; synced: commerce §4.9 and plan M11 earlier, S20 in commerce round 5) | 2026-10-19 | see below |
| Commerce round 5 (owner, 2026-10-19): GA7 recall trace, GA8 return reasons, GC9 dangerous goods land here; reference data is developed, not deployed (commerce D39) | ✅ decided; ⏳ to build | 2026-10-19 | §4.3, §4.5, §4.7; commerce §13 Round 5 |
| Spikes W1–W19 | ⏳ | — | §7 |
| Round 3 (owner, 2026-10-20): replication redesign: `[Replicated]` stays the single declaration, generated script + hash, owner approval gate, ETL or owner push by licence, change-vector concurrency (WD13–WD18); WIP b4c218eb's `[Replica]`/contract records rejected | ✅ decided; ⏳ to build (WM1) | 2026-10-20 | §4.2, §13 Round 3 |
| Milestones WM0–WM13 (inside commerce M20) | ⏳ | — | §10, plan |

The three commerce places ~~still~~ to sync with WD1/WD2 (all synced by 2026-10-19):
- ~~commerce §4.9 still says "Transport is not settled"~~ (now states the WD1/WD2 transport);
- ~~commerce plan M11 still builds the `Commerce.Fulfilment` contract on `[Replicated]` + SyncAction~~ (now the WD1 transport);
- ~~commerce S20 still expects "status write-back lands on the owner"~~ (struck in commerce round 5; status comes back as the warehouse's contract).

---

## 1. Goal

Ship **modular Spark libraries** that let a downstream developer host a **warehouse order-picking application**:
- receive goods into locations;
- keep stock true;
- pick, pack and ship orders;
- count stock;
- take returns back.

Orders come from a **separate** shop application, or from any app that speaks the generic fulfilment contracts. The owner fixed the two-app shape when commerce was grilled (D12):

> "in the near future (or now) I also want to ship a reusable, generic warehouse-order-picking system plugin. The production ticketsale-webshop-platform and warehouse-order-picking-platform used by the final consumers will be 2 different web-applications for sure, no discussion. ETL jobs are then used to consult information from the other app."

The two apps must exchange data on the licence production runs, and that licence rules out ETL:
- **RavenDB Community has no ETL** (`docs/endpoints_generator_adoption_plan.md:219`; `docs/multi-host-e2e-plan.md:130-132`);
- production runs Community 7.1.10 (commerce §8).

The second goal is therefore a **cross-app replication transport chosen by licence capability** (WD1):
- ETL when the licence has it;
- otherwise module-authenticated HTTPS push with reconciliation.

It becomes Spark's standard replication transport, and it also carries the reference-data app's consumer copies.

### Owner constraints

| # | Constraint | What it means here |
|---|---|---|
| K1 | Modular: many libraries, each with a concise purpose. | Twelve .NET packages and four Angular entry points (§4.1). |
| K2 | Behaviour is chosen by .NET configuration, not by different code (the owner said it of webshop versus ticketing). | Read the same way here: release mode, allocation rule, picking strategies, transport, printers and carriers are configuration (§4.13). |
| K3 | No backward compatibility: the libraries are in preview. | Replication is redesigned in place; Fleet and HR are rewired without shims (WD1). |
| K5 | One PR for everything, including every defect found. | This plugin lands inside the commerce PR as commerce M20, together with F12–F19. |
| K6 | A **framework**, not a platform: every policy is a configurable building block. | Warehouse policies are switches and strategy interfaces; `apps/Warehouse` shows one platform. |
| K7 | The framework does not pick vendors; the host chooses adapters in configuration. | Carriers (commerce D14), print adapters (WD7), the transport (WD1). |
| K9 | Redesigns are allowed, including in production; every VPS change is listed. | §8. Nothing in this PRD changes the VPS now. |
| K10 | Time zones are handled only in core built-ins. | Expiry is `DateOnly`. Wave cut-offs need a zone-aware schedule in core (WX14, Q-W1). |
| K11 | Standard web APIs first; polyfills only in the standard shape; a support matrix per feature. | Used: `BarcodeDetector` with a same-shape WASM ponyfill, IndexedDB, the `online` event, WebAuthn (including cross-device), Page Visibility, Screen Wake Lock, `navigator.storage.persist()`.<br>Not used: Web Serial and WebUSB (WD7); no reliance on Background Sync (WD6). |

K4 ("Work on this as long as you want; I want to ship a good plugin/product") and K8 (the reference-data app) apply as stated in the commerce PRD.

### Non-goals

See §9. In short, none of these:
- labour standards, slotting, yard, cross-docking or kitting;
- automation, purchasing or costing;
- 3PL billing;
- inter-site transfers;
- offline receiving or counting;
- device enrolment (WD10).

---

## 2. What Spark already gives us

Details and verdicts are in research §3. The design leans on these:

| Need | Spark feature | Evidence |
|---|---|---|
| Module identity on every cross-app call | Module client certificates: `AddModuleCertificateAuthentication`, a `Module:{CN}` principal governed by `security.json`, the thumbprint pinned on first registration. Modes: Auto, Production, Development, Disabled | `libs/replication/MintPlayer.Spark.Replication/Authentication/ModuleCertificateAuthentication.cs:49`; `Services/ModuleRegistrationService.cs:58-68`; `apps/Fleet/Fleet/Program.cs:20-47,100-104`; `docs/guide-replication-mtls.md:21-38` |
| TLS terminated at a proxy | A forwarded client-certificate header plus `KnownProxies` | `Authentication/ModuleCertificateForwarding.cs:9-35` |
| A write request into another module | SyncAction write-back: its own subscription plus HTTP, not the message bus. It works on Community | `Models/SparkSyncAction.cs:7`; `Workers/SyncActionSubscriptionWorker.cs:75-85` |
| An outbox in the save's own commit | `IAfterSaveCommitted`; `IMessageOutbox.EnqueueAsync(session, …)` | `libs/spark/MintPlayer.Spark.Abstractions/Interceptors/SparkCommittedInterceptors.cs:3-20`; `libs/messaging/MintPlayer.Spark.Messaging/IMessageOutbox.cs:19` |
| Retries that outlast a peer outage | `RetryAfterException` parks a message (five parks count as one attempt); the retry budget is set per queue | `libs/messaging/MintPlayer.Spark.Messaging.Abstractions/RetryAfterException.cs:3-14`; `libs/messaging/MintPlayer.Spark.Messaging/Services/MessageBus.cs:122` |
| Waves, reconciliation, count plans | 6-field cron, one run per occurrence across the cluster. It evaluates in UTC (§4.14) | `libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:239-273` |
| Atomic stock moves | An OCC retry loop under `OptimisticConcurrencyMode.Writes` | `libs/settings/MintPlayer.Spark.Settings/Quotas/SparkQuotas.cs:46-77` |
| Multi-document writes on a cluster | A cluster-wide transaction precedent | `libs/authorization/MintPlayer.Spark.Authorization/Identity/UserStore.cs:203-209` |
| Stable ids for locations, balances, movements | `IHasNaturalId` ("The id must not change") | `libs/model/MintPlayer.Spark.Model/IHasNaturalId.cs:35-41` |
| Stock owners (WD3) | Organizations: `IOrganizationOwned` plus its row policy. Raw session queries are not filtered | `libs/organizations/MintPlayer.Spark.Organizations/IOrganizationOwned.cs:21-29`; `Services/OrganizationRowPolicy.cs:28-31` |
| Live pick queues | `/spark/live` also sees writes from other processes, replica applies included. Cost: one re-evaluation per viewer per change; limit: 5 sockets per user | `docs/guide-live-updates.md:27-32,56,58` |
| Installable handheld app | The PWA provider. It caches the app shell only | `libs/node_packages/ng-spark/pwa/src/provide-spark-service-worker.ts`; `docs/guide-pwa.md:33-34` |
| Expiry dates without zones | `DateOnly` maps to the model type `date` | `libs/spark/MintPlayer.Spark.Abstractions/Model/SparkModelShape.cs:206` |
| Plugin-owned indexes | `AddIndexesFrom` inside a plugin's `AddX` | `libs/moderation/MintPlayer.Spark.Moderation/SparkModerationExtensions.cs:79` |
| Passkeys for shift sign-in (WD10) | The passkey sign-in endpoint | `libs/authorization/MintPlayer.Spark.Authorization/Endpoints/Passkeys/PasskeySignIn.cs:25`; `docs/guide-passkeys.md` |
| Planned in the same PR by commerce | • X2: a private per-identity stream<br>• X3: atomic natural-id create<br>• X4: `Money`, the basis for `Quantity`<br>• X5: `MintPlayer.Spark.Webhooks`, for carrier tracking<br>• X7: `ISaveReplacement`<br>• X12: claims (batch leases, tote uniqueness)<br>• X14: recipient locale<br>• X16: `<spark-date-time>` | commerce §5 |

**Gaps.** §5 and §6 close them:
- **No ETL on Community.** The existing design has more limits (research §3):
  - a compile-time `SourceModule` (`ReplicatedAttribute.cs:9,16,34`);
  - one replicated type per source collection;
  - ids preserved only within the same collection name, so two shops' ids collide;
  - deletes that propagate;
  - consumer-supplied ETL scripts (F16).

  Local development and CI run the Developer licence, so **an ETL design passes every local and CI test and then fails in production** (research §3).
- **Community caps revisions at 2 per document** (`docs/identity_provider_platform_PRD.md:391`). That rules out a revision-based change feed. It also rules out History as the stock audit trail, because History uses revisions (`libs/history/MintPlayer.Spark.History/README.md:3-6`).
- Also missing:
  - barcode scanning, anywhere;
  - 1D barcodes (QR only, inside Authorization);
  - a PDF library in `libs/`;
  - offline data;
  - a unit of measure;
  - a hierarchical reference picker (it is flat);
  - time zones in cron (UTC only);
  - idle sign-out.

---

## 3. Product scope

### 3.1 Personas

- **Platform host:** the developer or operator. Writes configuration and a handful of registration lines, and installs the print agent on site.
- **Stock owner:** an organization whose goods are stored (WD3). Its members see only its documents.
- **Warehouse staff.** Everyone signs in personally on every device, each shift (WD10). How roles are expressed is Q-W11. The roles:
  - receivers and put-away operators;
  - pickers (discrete, batch, cluster, zone) and consolidators;
  - packers;
  - counters;
  - returns clerks;
  - supervisors (exceptions, variance reviews, wave release).
- **Producer app:** the commerce shop (`Commerce.Fulfilment`), or any Spark app that publishes the fulfilment contracts (W8).
- **BYOD employee:** uses their own phone as a passkey authenticator or as the scanner (WD10b).

### 3.2 Scenarios the libraries must serve with configuration only

| Scenario | Owners (WD3) | Sites (WD3) | Pick work (WD5) | Transport (WD1) |
|---|---|---|---|---|
| A shop with its own stockroom | 1 | 1 | Discrete, immediate release | HTTP push (Community) |
| A brand warehouse serving several shops or channels | 1 | 1..n | Batch or cluster, waves at cut-offs | HTTP push, or ETL where licensed |
| A multi-owner warehouse (3PL-style, no billing) | n | 1..n | Cluster or zone with consolidation | One contract, several sources |
| A shop without a warehouse app | — | — | The shop prints labels itself (WD8) | None |
| A producer other than the commerce shop | 1..n | 1..n | Any | The same contracts (W8) |

The demo pair (§4.16) runs the first scenario locally. Proposal: seed two owners, to exercise WD3.

### 3.3 v1 at a glance

| Area | In v1 | Decided by |
|---|---|---|
| Master data | • Sites and zones<br>• Locations as separate documents (walk sequence, check digits)<br>• Warehouse extensions of shop items, warehouse-owned items, pre-items<br>• A barcode registry | WD3, WD8, WD9 |
| Stock | • A balance per (location, SKU, lot)<br>• An append-only ledger<br>• Reason-coded adjustments and moves<br>• Min/max top-up<br>• The stock feed to the shop | WD2, WD4, WD5 |
| Inbound | • Expected and blind receipts<br>• GS1 lot and expiry capture<br>• Put-away rules: fixed, same lot, nearest empty | WD4, WD5 |
| Outbound | • Intake with accept/reject; site routing<br>• Immediate or wave release<br>• FIFO, FEFO or closest allocation<br>• Discrete, batch, cluster and zone picking by walk sequence; consolidation<br>• Scan-to-confirm with a lot check<br>• Short picks, backorders, the cancellation handshake | WD2–WD5 |
| Packing | • Scan verification<br>• Rule-based cartonisation and weighing<br>• Carrier label and packing slip; optional SSCC<br>• Serials captured on the parcel<br>• Ship-confirm | WD4, WD5, WD8 |
| Counting | Blind ABC cycle counts (30/60/120 days) with variance review | WD5 |
| Returns | Receipt, inspection, disposition. By default, refunds wait for the receipt | WD5, WD12 |
| Devices | • Wedge and camera scanning<br>• A disconnect queue for assigned tasks<br>• Personal sign-in; BYOD phones | WD6, WD7, WD10, WD10b |
| Printing | PDF print dialog (default), an on-site agent, QZ Tray, Zebra Browser Print | WD7 |
| Metrics | Productivity events on the Dashboards plugin | WD5 |
| Integration | State replication both ways, one owner per fact, over the licence-chosen transport | WD1, WD2 |

### 3.4 What a host writes

Illustrative only. The final API is fixed in milestones WM1–WM9.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<WarehouseContext>();
    spark.AddAuthentication<SparkUser>();          // personal sign-in, passkeys (WD10)
    spark.AddModuleCertificateAuthentication();    // every cross-module call (WD1)
    spark.AddMessaging();
    spark.AddCron();
    spark.AddOrganizations();                      // stock owners (WD3)
    spark.AddReplication();                        // transport by licence capability: Spark:Replication (WD1)
    spark.AddWarehouse();                          // sites, locations, items, stock engine: Spark:Warehouse
    spark.AddWarehouseInbound();
    spark.AddWarehouseOutbound();
    spark.AddWarehousePacking();
    spark.AddWarehouseCounting();
    spark.AddShipping().UseSendcloud();            // carriers (WD8, commerce D14)
    spark.AddPrinting().UseAgent().UseQzTray();    // the PDF dialog is built in (WD7)
});
```

```jsonc
"Spark": {
  "Replication": {
    "Transport": "Auto",                                         // WD1: ETL if the licence has it, else Http
    "Consume": {
      "fulfilment-orders": { "Sources": [ "Shop" ], "Versions": [ 1 ] },
      "catalog-items":     { "Sources": [ "Shop" ], "Versions": [ 1 ] },
      "expected-returns":  { "Sources": [ "Shop" ], "Versions": [ 1 ] }
    }
  },
  "Warehouse": {
    "Release":    { "Mode": "Waves" },                           // cut-offs per site (Q-W1)
    "Allocation": { "Rule": "Fefo" },                            // overridable per owner and item (WD4)
    "Picking":    { "Strategies": [ "Cluster", "Zone" ], "Confirm": "ScanLocationAndItem" },
    "ShortPick":  { "Policy": "ReallocateThenBackorder", "TriggerCycleCount": true },
    "Packing":    { "Verify": "ScanEveryUnit", "Cartonization": "Volumetric", "Sscc": "Off" },
    "Counting":   { "Blind": true, "Abc": { "A": 30, "B": 60, "C": 120 } },
    "Offline":    { "Mode": "AssignedTasksOnly", "WarnAfter": "01:00:00" }
  },
  "Printing": {
    "Printers": {
      "pack-1": { "Adapter": "Agent", "Format": "Zpl203" },
      "office": { "Adapter": "Pdf" }
    }
  }
}
```

Client side:
- `provideSparkWarehouse(withInboundRoutes(), withOutboundRoutes(), withPackingRoutes(), withCountingRoutes())`;
- `provideSparkScanning()`, `provideSparkOffline()` and `provideSparkPrinting()`.

Any page can be replaced per route (commerce D27).

---

## 4. Architecture

### 4.1 Package map (commerce D31)

Generic packages are flat (D31):
- **Folders** under `libs/` keep short names, so paths stay under MAX_PATH. Project names are fully qualified.
- **Aliases** are lower-kebab and **permanent**. A package without persistent objects has no alias.

| Package | Alias | Folder | Purpose | Depends on | ng entry point |
|---|---|---|---|---|---|
| `MintPlayer.Spark.Fulfilment.Contracts` | — | `libs/fulfilment/contracts/` | ~~The versioned contract records both apps reference (§4.3).~~ One `[Replicated]` class per contract, stored by its owner and replicated to the host that binds it (§4.3, WM2 2026-10-20). No persistent objects, no Checkout | Replication.Abstractions | — |
| `MintPlayer.Spark.Gs1` | — | `libs/gs1/` | • GTIN and SSCC check digits<br>• SSCC allocation<br>• AI element-string and Digital Link parsing, with the AI dictionary as data<br>Generic: commerce validates GTINs with it | — | parser twin in `scanning` |
| `MintPlayer.Spark.Warehouse.Abstractions` | — | `libs/warehouse/abstractions/` | • Strategy interfaces: `IAllocationRule`, `IPutawayRule`, `IPickStrategy`, `IPickRouter`, `ICartonizer`, `IBarcodeResolutionStep`, `ISiteRouter`<br>• Value types | Abstractions | — |
| `MintPlayer.Spark.Warehouse` | `warehouse` | `libs/warehouse/core/` | • Sites, zones, locations<br>• Item extensions and pre-items<br>• The barcode registry and resolution<br>• The stock engine: balances, ledger, adjustments, moves, invariants<br>• Min/max replenishment<br>• Stock levels and the stock feed<br>• The task envelope<br>• Productivity events | Abstractions, Gs1, Organizations, Messaging, Cron, Replication, Fulfilment.Contracts | `warehouse` |
| `MintPlayer.Spark.Warehouse.Inbound` | `warehouse-inbound` | `libs/warehouse/inbound/` | • Expected and blind receipts<br>• Put-away rules and tasks<br>• Return receipt, inspection, disposition | Warehouse | `warehouse/inbound` |
| `MintPlayer.Spark.Warehouse.Outbound` | `warehouse-outbound` | `libs/warehouse/outbound/` | • Intake with accept/reject; site routing<br>• Release (immediate, waves) and allocation<br>• Discrete, batch, cluster and zone pick work; routing<br>• Scan-to-confirm; short picks; backorders<br>• The cancellation handshake<br>• Consolidation | Warehouse, Fulfilment.Contracts | `warehouse/outbound` |
| `MintPlayer.Spark.Warehouse.Packing` | `warehouse-packing` | `libs/warehouse/packing/` | • The pack station<br>• Cartonisation (manual, volumetric) and weighing<br>• Carrier labels and packing slips<br>• SSCC and serial capture<br>• Ship-confirm and handover | Outbound, Commerce.Shipping (abstractions only), Printing | `warehouse/packing` |
| `MintPlayer.Spark.Warehouse.Counting` | `warehouse-counting` | `libs/warehouse/counting/` | • Count plans (ABC 30/60/120 days, per location)<br>• Blind counts and spot counts<br>• Variance review, posting adjustments | Warehouse, Cron | `warehouse/counting` |
| `MintPlayer.Spark.Printing` | `printing` | `libs/printing/core/` | • Printers<br>• Print jobs with exactly-once acknowledgement<br>• Label templates and rendering (WX6)<br>• Delivery to the PDF print dialog, built in<br>• The agent's server endpoints | Messaging | `printing` |
| `MintPlayer.Spark.Printing.Agent` | — | `libs/printing/agent/` | The on-site .NET worker:<br>• an outbound connection to the app;<br>• ZPL or raw output to LAN printers (TCP 9100);<br>• optional scale reading;<br>• a local job journal;<br>• a heartbeat and an update check | client of Printing | — |
| `MintPlayer.Spark.Printing.QzTray` | — | `libs/printing/qz-tray/` | The QZ Tray adapter: a printer kind, plus the message-signing endpoint that silent printing needs | Printing | client in `printing` |
| `MintPlayer.Spark.Printing.ZebraBrowserPrint` | — | `libs/printing/zebra/` | The Zebra Browser Print adapter: a printer kind and its delivery | Printing | client in `printing` |
| `@mintplayer/ng-spark/scanning` | — | `libs/node_packages/ng-spark/scanning/` | • Wedge capture<br>• Camera: `BarcodeDetector` plus a same-shape WASM ponyfill<br>• GS1 parsing; AZERTY detection; scan-driven steps<br>**Shared with the commerce scanner PWA** | — | — |
| `@mintplayer/ng-spark/offline` | — | `libs/node_packages/ng-spark/offline/` | • An IndexedDB command queue with op ids<br>• Replay; exceptions<br>• Wipe on sign-out<br>**Shared with the commerce scanner** | `pwa` | — |
| `@mintplayer/ng-spark/printing` | — | `libs/node_packages/ng-spark/printing/` | • Client adapters: PDF print dialog, QZ Tray, Zebra Browser Print<br>• Job delivery to the station | — | — |
| `@mintplayer/ng-spark/warehouse` (+ `/inbound`, `/outbound`, `/packing`, `/counting`) | — | `libs/node_packages/ng-spark/warehouse/` | • Task-directed handheld pages<br>• The pack-station page<br>• Supervisor pages<br>• The barcode-choice dialog<br>Replaceable per route (commerce D27) | `scanning`, `offline`, `printing` | — |

**Changes from the research proposal (research §7):**
- **Replenishment lives in the core**, not in Counting. Min/max top-ups are moves, triggered both by a daily sweep and by short picks (Outbound). Counting keeps counts only.
- **`MintPlayer.Spark.Printing.Agent` is the on-site worker.** The agent's server endpoints live in `Printing`; the research had `.Agent` plus `.AgentHost`.
- **No `.RawTcp` or `.Zpl` package.** ZPL building is part of label rendering in `Printing`, and raw TCP printing is the agent's job (Q-W22).
- **WX12 is already done** in the commerce package map: `Commerce.Shipping` has no Checkout dependency (commerce §4.1).

**Names.** Entity names must be unique across loaded assemblies, because actions resolve by simple class name and two matches throw (`libs/spark/MintPlayer.Spark/Services/ActionsResolver.cs:142,151-161`).
- Used: `WarehouseLocation`, `StockBalance`, `StockMovement`, `OutboundOrder`, `WarehouseTask`, `WarehouseShipment`.
- Never: `Location`, `Order` or `Shipment`.

Commerce X9 adds the collision analyzer.

**Reference sets:**
- **Warehouse host:** Warehouse, Inbound, Outbound, Packing, Counting, Printing (plus adapters), Commerce.Shipping (plus a carrier), Replication, Organizations, Fulfilment.Contracts.
- **Shop host:** commerce's webshop set; `Commerce.Fulfilment` brings in Fulfilment.Contracts.

**Coupling rule:**
- The warehouse references no commerce package except `Commerce.Shipping` (carriers, labels, tracking). W8 checks this by assembly reference.
- Commerce references no warehouse package.
- The two meet only in `Fulfilment.Contracts`.

### 4.2 Replication: one declaration, an approval gate, the transport chosen by licence (WD1, WD13–WD18)

> **Rewritten 2026-10-20 (owner round 3, §13).** The contract design this section held until then (hand-written versioned contract records, `Publish<TSource, TContract>`, `Consume:{contract}` configuration, the `[Replica]` rename of the interrupted WIP commit b4c218eb) is **superseded**; its decisions are kept, struck, at the end of this section. Evidence notation: `path:line` is the working tree at HEAD (b4c218eb); `path:line @e0c542f9` is a file the WIP commit deleted or broke, cited from the last good commit.

**What exists (e0c542f9).**
- The consumer declares `[Replicated(SourceModule, Contract, SourceCollection)]` on a read-only copy class (`libs/replication/MintPlayer.Spark.Replication.Abstractions/ReplicatedAttribute.cs:14-42 @e0c542f9`). Since 43d492df the owner also declares a hand-written record per type and a `Publish` lambda (`apps/Fleet/Fleet.Library/Contracts/CarV1.cs`; `apps/HR/HR/Program.cs:86-92`; `apps/Fleet/Fleet/Program.cs:56-60`), and compiles the lambda to an ETL script at runtime (`libs/replication/MintPlayer.Spark.Replication/Services/EtlTransformGenerator.cs:74-92 @e0c542f9`). Every replicated field is therefore written twice. That is the duplication the owner rejected.
- At startup the consumer broadcasts a deployment request per owner (`Extensions/SparkReplicationExtensions.cs:113-115 @e0c542f9`). A messaging recipient posts it over mTLS and treats 400/404/409 as final (`Messages/EtlScriptDeploymentRecipient.cs:43-48 @e0c542f9`). The owner checks the `Replicate/{SourceCollection}` grant (`Endpoints/EtlDeploy.cs:101 @e0c542f9`), takes the destination from the registry (F19 fixed, `:115-129`), and deploys one task `spark-etl-{Module}` (`Services/EtlTaskManager.cs:49-50,112,119 @e0c542f9`). It posts on **every** start and redeploys on **every** start; there is no hash and no approval.
- Write-back runs on its own RavenDB subscription (`Workers/SyncActionSubscriptionWorker.cs:36`) plus a sweeper (`Services/SyncActionRetrySweeper.cs:85-92`). The default app with messaging and replication therefore uses 2 of Community's 3 subscriptions (`libs/messaging/MintPlayer.Spark.Messaging/SparkMessagingOptions.cs:195-212`; `libs/subscription_worker/MintPlayer.Spark.SubscriptionWorker.Abstractions/SparkSubscriptionWorker.cs:105`).
- The owner applies an edit on top of the version stored **now**, so a stale replica edit silently overwrites newer owner changes: "The replica's change vector means nothing here" (`libs/spark/MintPlayer.Spark/Services/SyncActionHandler.cs:43-53,63 @e0c542f9`; still so in the WIP, `SyncActionHandler.cs:39-47,60`). A replica stores nothing about the source version it was built from (no change vector in the generated script, `EtlTransformGenerator.cs:74-92 @e0c542f9`).

**Decision (owner round 3, 2026-10-20).**

| # | Rule |
|---|---|
| WD13 | `[Replicated]` stays, and the **replica class is the single declaration**. No contract records, no `Publish`, no `[Replica]` |
| WD14 | The replication script is **source-generated** into the `partial` replica class as `const string`s (script and hash). No runtime reflection |
| WD15 | Scripts are hashed on **both** sides: the consumer doesn't post an unchanged script, the owner doesn't redeploy an unchanged approved one |
| WD16 | The **owner** approves. Auto-approval only when the owner's host environment is Development, never by build configuration. Destinations come from the authenticated module's registry entry (F19) |
| WD17 | Owner licence has ETL: the approved script runs as a RavenDB ETL task. No ETL (Community, production): the owner pushes the same projection over Spark messaging and the mTLS channel, both directions share that plumbing, and a Spark cron job reconciles |
| WD18 | ~~Write-back reuses Spark's optimistic concurrency. The replica carries the source change vector it was built from; a write-back uses it as `BaseEtag`, so a stale edit is a **Conflict** that reaches the initiator.~~ **Superseded for write-back by WD19 (owner decision 2026-10-20).** Still in force: pushes never apply an older projection over a newer one (the source change vector guards the push direction) |
| WD19 | Write-back is a **per-property compare-and-set** inside the owner's normal save path (`ISparkReplacedSaves`: rights incl. attribute rights, validation, interceptors). Each changed property carries the base the person started from; equal → applied, different → that property is a Conflict, the whole action is refused, nothing is applied; unlisted properties never conflict; collections and AsDetail compare as whole values. Answered with Spark's concurrency vocabulary (`SparkConflictException` reasons, the 409). Owner decision 2026-10-20 |
| WD20 | The save of a write-back replica calls the owner **synchronously** over the mTLS channel (5 s, configurable); the owner's answer is the save's answer. Only an unreachable or slow owner queues the SyncAction (same commit) and the user is told "saved, pending at {owner}". Deferred terminal outcomes reach the initiator durably and live; GaveUp also mails. Write-back edits are **never applied locally**. Owner decision 2026-10-20 |
| WD21 | Administrators approve replication requests on the owner's website: the library's "Replication requests" program unit (pending first; consumer, source, state, requested vs approved hash, readable diff; Approve/Reject custom actions; `replication:approvers` slot, hidden from users without the read right; auto-approved rows marked). Owner decision 2026-10-20 |

#### 4.2.1 The declaration and the generator

```csharp
// HR.Library: a read-only copy of Fleet's cars. Nothing else declares this shape.
[Replicated(SourceCollection = "Cars", SourceModule = "Fleet", WriteBack = true)]
public partial class Car
{
    public string? Id { get; set; }
    public string LicensePlate { get; set; } = "";
    public string Model { get; set; } = "";
    [ReplicatedFrom("Owner.Name")] public string? OwnerName { get; set; }   // rename / nested path
}
```

**Attribute arguments** (`ReplicatedAttribute`, restored from e0c542f9 and reworked):

| Argument | Meaning | Default |
|---|---|---|
| `SourceCollection` (required) | The owner's RavenDB collection the script reads. The generator can't know the owner's naming conventions, so it is never inferred | — |
| `SourceModule` | The owner module. **Optional** (owner decision 2026-10-20): a **generic library** (e.g. `Fulfilment.Contracts`) leaves it out and the host names it in `Spark:Replication:Sources:{TargetCollection}` (one or more modules; ids must be unique across them, an id held by another source is refused as poison). **Startup refuses** a replica that neither names (`ReplicaCatalog.Problems`) | config |
| `TargetCollection` | The consumer's collection. Defaults to the generator's pluralisation of the class name (Inflector-like: `Person` → `People`); startup refuses when the store names the collection differently, with the value to set (implementation decision, WM1) | pluralised name |
| `OriginalType` | Optional `typeof(owner type)` when the consumer can reference the owner's library. The generator then checks every mapped path against it (SPARK067) | none |
| `WriteBack` | Whether edits of this replica go back to the owner as SyncActions. False: the type is fully read-only | `false` |
| `[ReplicatedFrom("A.B")]` on a property | Source path when it differs from the property name | the property name |

`Contract`/`Version` are removed: a new shape is a new replica class in a new target collection, so versions still run side by side without a version number.

**What the generator emits** (in `MintPlayer.Spark.LibraryGenerators`, the entity-library generator, because replica classes live in entity libraries; modelled on `ValueObjectKeyGenerator`: `ForAttributeWithMetadataName`, an equatable model without symbols, a `Producer`, a reporter, `libs/source_generators/MintPlayer.Spark.LibraryGenerators/Generators/ValueObjectKeyGenerator.cs:30-64,72-86`):

```csharp
partial class Car : MintPlayer.Spark.Replication.Abstractions.IReplicatedEntity
{
    public const string ReplicationSourceCollection = "Cars";
    public const string ReplicationTargetCollection = "Cars";        // the consumer's collection for Car
    public const string ReplicationManifest = "{\"v\":1,\"fields\":[[\"LicensePlate\",\"LicensePlate\"],…]}";
    public const string ReplicationScript = "loadToCars({ LicensePlate: this.LicensePlate, …, '@metadata': {…} });";
    public const string ReplicationHash = "sha256:…";                 // over v1 + source + target + manifest + script
}
// plus one [assembly: SparkReplicaTypes(typeof(Car), …)] per library, so the runtime lists replicas without scanning members
```

**As built (WM1, 2026-10-20).** `IReplicatedEntity` is a marker: a `const` cannot implement a static abstract member, so the runtime reads the generated literals (`FieldInfo.GetRawConstantValue`, no member scanning, no script building). The generator also emits `ReplicationSourceModule` (null when not given) and `ReplicationWriteBack`. The manifest JSON carries `v`, `source`, `target` and the fields; the hash is `sha256` over `manifest + "\n" + script`. The ETL variant of the script (`RenderEtlScript`) adds `Spark-Replica-Source-Module` and is rendered by the owner at deploy, so the posted script stays module-independent. Inherited properties are mapped (base first). Files: `libs/replication/MintPlayer.Spark.Replication.Abstractions/Rendering/*`, `libs/source_generators/MintPlayer.Spark.LibraryGenerators/Generators/ReplicatedGenerator*.cs`, `ReplicaCollectionNames.cs`, `CodeFixes/ReplicatedPartialCodeFixProvider.cs`.

- Every public instance property with a setter, except `Id`, is mapped. Replicas hold no consumer state (consumer state lives in consumer documents keyed by the replica id, as before).
- The **manifest** is the structured field list (target ← source path). The **script** is its JavaScript rendering: null-safe member paths only, identifiers checked against `^[A-Za-z_][A-Za-z0-9_]*$` (the escaping rules of `EtlTransformGenerator.cs @e0c542f9`, moved), plus `'@metadata': {'@collection', 'Spark-Replica-Source-Change-Vector', 'Spark-Replica-Source-Module'}` (W6 verifies ETL may set custom metadata; fallback: one reserved body field `SparkReplicaSource`, which Raven keeps without a CLR member).
- The renderer is **one source file** compiled into both the generator (netstandard2.0, `SHA256.Create().ComputeHash` + own hex) and `Replication.Abstractions`, so the owner can re-render a manifest byte-for-byte.
- **Diagnostics** (next free id is SPARK065: the highest in use is SPARK064, `libs/source_generators/MintPlayer.Spark.SourceGenerators/Diagnostics/SparkLookupDiagnostics.cs:15`; registry `docs/diagnostics.md:24-89`):

  | Id | Severity | When |
  |---|---|---|
  | SPARK065 | Error | `[Replicated]` class not `partial` (code fix adds `partial`, as `CodeFixes/ValueObjectPartialCodeFixProvider.cs:32`). Error, not a runtime fallback: without the generated constants the runtime has no script (WD14) |
  | SPARK066 | Error | A mapped member or path segment is not a plain identifier, or `SourceCollection` is missing/invalid |
  | SPARK067 | Error | `OriginalType` given and a mapped path does not exist on it |
  | SPARK068 | Error | A hand-written member collides with a generated one (`ReplicationScript` …). **There is no hand-written script override**: a consumer-authored script is exactly F16, and it couldn't run on the HTTP path. A derived value is computed by the consumer from the replica |

#### 4.2.2 Registration, hashing and the approval gate

```
consumer start ──(set hash changed?)──► outbox message ──mTLS──► owner /spark/replicas/register
                                                                   │ grant + attribute read rights per field
                                                                   │ re-render manifest == posted script/hash?
                                                                   ▼
                                    SparkReplicationSubscription (persistent object, one per consumer × target collection)
                                       hash == ApprovedHash ─► "unchanged", nothing redeployed
                                       else Pending ─► Development? auto-approve : admin Approve/Reject (custom actions, diff)
                                                         └► on approve: messaging ─► deploy (ETL task | HTTP subscription + initial reconcile)
```

**Consumer.**
- At startup it groups its replica types by owner module and computes a **set hash** over their `ReplicationHash`es. It stores the last hash the owner acknowledged in `SparkReplicaRegistrations/{owner}`; if unchanged and acknowledged as approved, it posts nothing (WD15).
- Otherwise it enqueues a `ReplicaRegistrationMessage` through the messaging outbox (durable, the same pattern as `EtlScriptDeploymentRecipient @e0c542f9`): owner not registered or unreachable → `RetryAfterException`; 400/403/404 → `NonRetryableException` with the reason logged (`libs/messaging/MintPlayer.Spark.Messaging.Abstractions/RetryAfterException.cs:10,22`).
- The post lists **all** replicas the consumer reads from that owner: `{targetCollection, sourceCollection, manifest, script, hash}`. The requesting module comes from the certificate, never the body; the destination database is never sent (F19).

**Owner, `POST /spark/replicas/register`** (mTLS gate as `Endpoints/EtlDeploy.cs:63-79 @e0c542f9`):
1. Grant: `Replicate/{SourceCollection}` for `Module:{consumer}` (`EtlDeploy.cs:101 @e0c542f9`; WIP `Services/ModuleGrants.cs`). **Attribute rights** too: a manifest field the module may not read is refused by name (F17, read side; "ETL script reads beyond its grant").
2. Re-render the script from the manifest with the shared renderer; refuse (400) unless it equals the posted script and hash. The owner therefore never deploys consumer-authored JavaScript (F16 closed by construction), and the manifest grammar has no `loadDocument`.
3. Upsert `SparkReplicationSubscriptions/{consumer}/{targetCollection}`: `PendingHash/Manifest/Script`, `ApprovedHash/Manifest/Script`, `ApprovedBy/At`, `Status` (`Pending | Approved | Rejected | Withdrawn | Suspended`), `Deployed` (transport, destination, hash).
4. Posted hash == `ApprovedHash` → `unchanged`; nothing is redeployed (WD15). Otherwise `Pending`, and:
   - owner `IHostEnvironment.IsDevelopment()` → approved at once, by the system, recorded as such (precedent for environment gating: `libs/identity_provider/MintPlayer.Spark.IdentityProvider/Services/OidcSigningKeyService.cs:39`). No `#if DEBUG`, no configuration switch (the reference system's flaw, §13);
   - otherwise an administrator decides.
5. Subscriptions of this consumer **missing** from the post become `Withdrawn`: the ETL transform is removed or pushes stop (F18 cleanup).
6. Answer per item: `approved | unchanged | pending | refused(reason)`. The consumer stores the answer; `pending` items get data once approved, without a re-post.

**The gate is built from Spark features** (Q-W27 decided 2026-10-20: the replication library's own persistent object, no dependency on `libs/approvals`; WD21). As built:
- **Two documents.** `SparkReplicationRequest` (`SparkReplicationRequests/{consumer}/{target}/{hash16}`, so re-posting a shape is idempotent) is the library persistent object administrators see; states `Pending | Approved | Rejected | Superseded` (a newer pending request, or a newer approval, supersedes). `SparkReplicationSubscription` (`SparkReplicationSubscriptions/{consumer}/{target}`) is internal delivery bookkeeping: `Active | Withdrawn | Suspended`, approved manifest/hash, the pending request, `Deployed (transport, hash, destination)`.
- **Program unit "Replication requests"** shipped in the library's App_Data layer (`SparkLibraryAlias` `replication`: `Model/SparkReplicationRequest.json`, `programUnits.json`, `actions.json`, `translations.json`, `security.json`), query `ReplicationRequests` with pending requests first (stored `IsPending`), then newest. The detail shows consumer module, source and target collection, status, auto-approved flag, requested vs approved hash, the readable **diff** (`+ field ← path`, `- field`, `~ field ← newpath (was oldpath)`, collection changes) and the manifest. Auto-approved (Development) rows stay listed, marked `AutoApproved` with approver `system (Development)`.
- **Rights:** the library's `security.json` grants `QueryRead`, `ApproveReplication` and `RejectReplication` on `SparkReplicationRequest` to the slot `replication:approvers`; each application binds it (`"bindings": { "replication:approvers": [ "Administrators" ] }`, HR and Fleet do). A user without the read right does not see the program unit. Approve/Reject are offered only on a pending row (`SparkReplicationRequestActions.OnDisableActionsAsync`). Library rights are a layer since composition D16, so the old "a package cannot ship rights" limit no longer applies.
- Approving re-checks the grant and read rights (§4.2.6) and enqueues a `ReplicaDeployMessage` in the same commit; the deploy runs from messaging, so it retries and survives restarts. Reject suspends the subscription and removes its delivery; re-posting a rejected shape is answered `Refused`.
- ~~Q-W27 asks whether to use `libs/approvals` instead.~~ Decided (above).

**Deploy** (`ReplicaDeployMessage` recipient, owner):
- Transport per owner: `Spark:Replication:Transport` = `Auto | Http | Etl`, resolved by `ReplicationTransportResolver` (WIP, `Services/ReplicationTransportResolver.cs:33-65`) over `ISparkLicenseCapabilities` (WIP, `libs/spark/MintPlayer.Spark/Services/SparkLicenseCapabilities.cs:24-47,99,121`: `GET /license/status` → `HasRavenEtl`, cached; unreadable → unknown → `Auto` takes HTTP). `Etl` pinned without the licence refuses startup (F18). The consumer needs no transport setting: it always exposes the apply endpoint.
- ETL: one task per consumer (`spark-etl-{consumer}`), with one transform per approved subscription; the connection string comes from the consumer's registry entry (`EtlDeploy.cs:115-129 @e0c542f9`). It is skipped when the deployed `(transport, destination, hash set)` is unchanged; a changed registry URL or database therefore redeploys. Withdrawn/rejected/revoked transforms are removed (WIP `EtlTaskManager.RemoveAsync`).
- HTTP: the subscription becomes active and an **initial reconcile** runs (§4.2.4).

#### 4.2.3 HTTP path (no ETL): the owner pushes

**Projection without ETL.** The owner interprets the approved **manifest** in C#: it reads the source document as raw JSON and copies the listed paths with the script's null-safe semantics. Considered and rejected:
- a RavenDB JavaScript `select` projection query: the script would be spliced into RQL (repo rule: never), and it would run consumer-shaped JavaScript;
- a generated C# projection on the owner: the owner does not reference the consumer's classes;
- running the script in an embedded JS engine: a second engine with subtly different semantics.

A test asserts the manifest projector and the ETL script give the same document for the same source (W6, Developer licence).

**Capture (owner).**
- An `IAfterSaveCommitted` / `IAfterDeleteCommitted` interceptor on every collection with an active HTTP subscription. The framework stores its work message **in the save's own session** (`libs/spark/MintPlayer.Spark.Abstractions/Interceptors/SparkCommittedInterceptors.cs:104-114`; `libs/spark/MintPlayer.Spark/Services/SparkInterceptorPipeline.cs:176-210`), so capture is durable without a new subscription; the interceptor runs later, at least once (`SparkCommittedInterceptors.cs:16`).
- It broadcasts one `ReplicaPushMessage {consumer, targetCollection, documentId}` per subscribed consumer (`libs/messaging/MintPlayer.Spark.Messaging.Abstractions/IMessageBus.cs:10`) on the queue `spark-replication` (declared by the library, `libs/messaging/MintPlayer.Spark.Messaging/Services/MessageBus.cs:158-177`).
- Raw session writes bypass interceptors: library code calls `IReplicaPublisher.MarkChanged(session, id)` (WIP `ReplicaChanged.cs`), and reconcile catches the rest.

**Delivery (owner).** The recipient **loads the current document** and its change vector (so a burst of writes becomes one push of the latest state), projects it, and posts `POST /spark/replicas/apply` to the consumer's `AppUrl` **from the registry** (F19; `ModuleInformation.cs:17-30`) with the module certificate (`Services/ReplicationHttpClientProvider.cs:16-69`). Body: `{targetCollection, hash, items: [{id, sourceModule, sourceChangeVector, payload | deleted}]}`.
- unreachable / 5xx / 429 → `RetryAfterException`; 4xx → `NonRetryableException`, dead-lettered with its reason;
- `409 manifest-mismatch` (the push's hash is neither the consumer's current nor its last approved one, e.g. after a rejection) → dead-letter; the reconcile after the next approval repairs it;
- budget and backoff per queue (`libs/messaging/MintPlayer.Spark.Messaging/SparkQueueOptions.cs:54-76`; global defaults `SparkMessagingOptions.cs:40,50,75-89`): the library default is the reference system's ladder 1 m / 5 m / 30 m / 2 h, then every 2 h, with `ExpiresAtUtc` at 48 h (RetryAfterException parks count as attempts only every `RateLimitParksPerAttempt`, `RetryAfterException.cs:10`).
- Ordering is not needed (the version guard decides), so the queue may run with `MaxConcurrency` > 1 (`SparkQueueOptions.cs:54-58`). W5 checks that a parked push for one dead consumer doesn't delay the others.
- High-churn collections (stock levels) flush through the WIP dirty set (`ReplicaDirtyMarker`, `Models/ReplicationDocuments.cs`) every 1–2 s from a cron job, because the outbox refuses `DeduplicationKey` (`libs/messaging/MintPlayer.Spark.Messaging/IMessageOutbox.cs:16-17`).

**Apply (consumer), `POST /spark/replicas/apply`:**
1. mTLS; the caller must be a source module of `targetCollection` (attribute or `Sources` config); the posted `hash` must equal the replica class's `ReplicationHash` **or** the hash the owner last acknowledged as approved (so an upgraded consumer keeps receiving the approved shape while its new manifest waits; Q-W29 decided 2026-10-20), else `409 manifest-mismatch`.
2. Per item, in one session with optimistic concurrency on the replica: load the replica and its tombstone (`ReplicaTombstone`, WIP `ReplicationDocuments.cs`), compare **change vectors** with a new core helper `SparkChangeVector.Compare(a, b)` → `Older | Equal | Newer | Concurrent` (entry-wise over `TAG:etag-dbid`):
   - `Newer` → store the payload with metadata `Spark-Replica-Source-Change-Vector` and `-Source-Module` (WIP `ReplicationDocuments.cs:21`), or delete it and keep a tombstone;
   - `Equal` / `Older` → no-op, still 200 (idempotent: a resend after a lost 200 is harmless);
   - `Concurrent` → no-op plus a reconcile hint (only a conflicted multi-node owner produces it; the owner's next state dominates both);
   - an id whose replica came from another source module → refused as poison.
3. In the same session, a durable `IReplicaChanged<T>` (previous and new state, WIP `ReplicaChanged.cs`) is enqueued through the outbox.
4. Tombstones expire after 7 days (> the 48 h budget + one reconcile period), using RavenDB document expiration (W6 confirms it on Community).

Replicas are **read-only** to everything but this endpoint and ETL: `DatabaseAccess` refuses unchecked writes to replica types (WIP `ISparkReplicaTypes`, `DatabaseAccess.EnsureNotReplica`); the model grants no `New`/`Delete`, and `Edit` only with `WriteBack = true`, which becomes a SyncAction (§4.2.5).

#### 4.2.4 Reconcile (HTTP path only)

- An **owner** cron job (`ISparkCronJob`, `libs/cron/MintPlayer.Spark.Cron/ISparkCronJob.cs:12,19,37`; one run per occurrence across the cluster through a compare-exchange claim, no subscription, `SparkCronScheduler.cs:215-260`), default hourly (`Spark:Replication:Reconcile`), and once per subscription right after approval (the initial load) or after a restore.
- Per active HTTP subscription it merge-joins two id-ordered streams: the owner's `(id, change vector)` of `SourceCollection` (collection name through `RqlIdentifier.Collection`, never spliced) and the consumer's `GET /spark/replicas/digest?collection=&after=&take=` pages `(id, sourceChangeVector, tombstone)`.
- Missing or older at the consumer → a `ReplicaPushMessage`; present at the consumer but gone at the owner → a tombstone push. Pushes reuse delivery unchanged, so the reconcile adds no second write path.
- The ETL path doesn't need it (WD1 refined): RavenDB ETL resends from its own etag.

#### 4.2.5 Write path: per-property compare-and-set, synchronous first (WD19, WD20)

> **Rewritten 2026-10-20 (owner's final decisions, §13 round 3).** The "source change vector as `BaseEtag`" design below the line is **superseded** for write-back: a whole-document base made an edit of one field conflict with any unrelated owner change. The change vector still guards the push direction (§4.2.3).

```
replica save ─ ISaveReplacement ─► SyncApplyRequest {OpId, changes: [{property, base, value}], initiator}
           ─ mTLS, within the request (5 s) ─► owner /spark/sync/apply ─► ISyncActionHandler (CAS) ─► ISparkReplacedSaves
           ◄── applied | conflict(changed|deleted, attributes) | refused(forbidden|invalid) ──┘  = the save's own answer
   owner unreachable / 5xx / timeout ─► SparkSyncAction + SyncActionDispatchMessage, same commit ─► "saved, pending at {owner}"
           ─ messaging (ladder 1 m/5 m/30 m/2 h, 48 h) ─► Completed | Conflict | Refused | GaveUp ─► initiator told (durable + live, GaveUp mailed)
```

- **Capture (consumer).** `ReplicaWriteBackInterceptor` is the `ISaveReplacement`/`IDeleteReplacement` of every replica type (`libs/replication/MintPlayer.Spark.Replication/Services/ReplicaWriteBack.cs`). The changed properties are the posted object's `IsValueChanged` attributes (as `SyncAction.Properties` were); each carries its **base** (the stored replica's value, which the framework's etag check has just verified the person saw) and its new value (the posted object through `IEntityMapper.ToEntity`), both as JSON in the store's serialization, keyed by the owner's property name. A field read from a nested path cannot be written back (refused naming it); a replica that is not `WriteBack` is read-only; a replica is never created here. A delete carries every writable field as base.
- **Synchronous first (WD20).** The save posts to the owner's `/spark/sync/apply` with `Spark:Replication:WriteBack:SynchronousTimeout` (5 s). Applied → `202 { submitted: { kind: "replication" } }`; conflict → `SparkConflictException` (now a public base of the framework's concurrency exception, so the 409 body is the one a person gets, plus `attributes`); forbidden/invalid → `SparkValidationException` naming the replica's attribute. Only no answer (unreachable, 5xx, 429, timeout) queues: the `SparkSyncAction` (`SparkSyncActions/{opId}`, changes stored as JSON text because the store serializes with Newtonsoft) and its `SyncActionDispatchMessage` go into the **save's own session** through `IMessageOutbox` (F13), and the answer is `202 { kind: "replication-pending", referenceId }`. Write-back edits are **never applied locally**: the replica changes only when the owner's delivery arrives.
- **Owner (WD19).** `SyncApplyService` resolves the source collection to a model type (`ReplicaSourceTypes`, model types only), answers a replayed op id from `SyncInboxEntry` (F15; written with the edit through `SyncEdit.Alongside`, or right after a refusal), and calls `ISyncActionHandler.ApplyEditAsync(SyncEdit { Values, BaseValues, InitiatorId, Alongside })`. The handler (`libs/spark/MintPlayer.Spark/Services/SyncActionHandler.cs`) loads the row, compares each listed property's stored JSON with its base (`JsonElement.DeepEquals`; absent = null), returns `Conflict(changed, attributes)` on any difference (nothing applied), else saves through `ISparkReplacedSaves.ApplyAsync` with the change vector the compare read as `BaseEtag`; a write landing in between is compared again (3 rounds). A deleted row is `Conflict(deleted)`. Deletes go through `IDatabaseAccess.DeletePersistentObjectAsync` with the same compare. Status codes: 200 applied, 409 conflict, 403 forbidden, 422 invalid, 400 otherwise; never 207 (F12).
- **One pending change per row (Q-W28, decided 2026-10-20).** A `SyncPendingMarker` (`SparkSyncPending/{documentId}`) is written in the save's commit. It counts while its queued SyncAction runs, and, once the owner applied the change, until the delivery rewrites the replica (the replica's own change vector moves; 1 h expiry as a backstop), on either transport. A second edit meanwhile is refused locally: "A change to this row is still pending at {owner}."
- **Deferred outcomes (WD20).** `SyncActionDispatchRecipient` sends the stored request with its op id; applied → `Completed` (the badge stays until the delivery); conflict/refused → `Conflict`/`Refused`; no answer → `RetryAfterException` on the ladder until `GiveUpAtUtc` (48 h) → `GaveUp`. A terminal failure removes the pending mark and `SyncOutcomeNotifier` tells the initiator: the `SparkSyncAction` itself is the durable per-user record ("My pending changes" query, the initiator's rows only, until dismissed — so it shows at the next sign-in), a `SyncOutcomeFrame` goes to `PublishPrivate("user:{id}", "replication:outcomes")` (live topic policy `replication:`, private, signed-in only), `GaveUp` is mailed through `ISparkMailer` when MailManager is registered (template `SparkReplication/WriteBackGaveUp`, the user document read as JSON so replication does not depend on Authorization), and every `ISyncActionOutcomeObserver` hears it (e.g. to add a Notifications entry). `RetrySyncAction` re-drives a GaveUp edit; `DismissSyncAction` removes an ended one from the list.
- **The pending state on the row.** `metadata.replication = { pending, owner, syncActionId, own }` (`IAfterLoad`), drawn by ng-spark's `provideSparkReplication()` as a title badge; `<spark-pending-changes-link>` in the shell shows the count of "My pending changes" live and turns red when a failure is pushed (`@mintplayer/ng-spark/replication`).
- **No echo.** The replica is never saved locally, so the owner's push of the applied state is the only change.
- **Subscriptions.** `SyncActionSubscriptionWorker`, `SyncActionRetrySweeper` and `SparkSyncActions_ByStatus` are deleted. Replication needs **no subscription of its own** and registers no hosted service.

<details>
<summary>Superseded 2026-10-20: write-back with the source change vector as <code>BaseEtag</code></summary>


```
replica edit ─ ISaveReplacement ─► SparkSyncAction {OpId, BaseEtag = replica's source CV, values, initiator}
                                   + outbox message, same commit (F13)
           ─ messaging ─mTLS─► owner /spark/sync/apply ─► ISparkReplacedSaves (BaseEtag) ─► Applied | Conflict | Refused
           ◄──────── outcome per action ────────┘        Applied: the owner's push (or ETL) updates every replica, requester included
```

- **Capture (consumer).** A replica edit is replaced, not saved (commerce X7 `ISaveReplacement`, `libs/spark/MintPlayer.Spark.Abstractions/Interceptors/SparkSaveReplacement.cs:28`); a replica delete likewise (`IDeleteReplacement`, `Interceptors/SparkInterceptors.cs:117`). The replacement stores a `SparkSyncAction` (WIP model, plus `BaseEtag`) and a `SyncActionDispatchMessage` through `IMessageOutbox.EnqueueAsync(session, …)` (`IMessageOutbox.cs:19`) in the edit's own session: durable (F13). Today the record is written after the commit in a new session (`Services/SyncActionInterceptor.cs:149-164`), and the replica is saved locally first (`Services/ReplicationInterceptor.cs:16,28-34`).
- **The base is the source version the replica was built from**, read from its `Spark-Replica-Source-Change-Vector`. The owner hands it to `ISparkReplacedSaves.ApplyAsync` as `BaseEtag` (`SparkSaveReplacement.cs:59-63,90-105`; `libs/spark/MintPlayer.Spark/Services/SparkReplacedSaves.cs:61,69,83,109-112`), which stores with that expected change vector (`libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs:862,906-907`) and maps Raven's conflict to `SparkConcurrencyException` `changed`/`deleted` (`libs/spark/MintPlayer.Spark/Exceptions/SparkConcurrencyException.cs:15,18,37`), the same 409 a person gets (`docs/guide-concurrency.md:14-16,75-76`). This replaces "the version stored now" (`SyncActionHandler.cs:39-47,60`).
- **One pending action per replica document.** While a SyncAction for a document is pending, a second edit is refused locally ("an edit is pending at {owner}"). This gives per-document ordering (F15) without a sequence: the second edit would carry the same stale base anyway.
- **Owner, `POST /spark/sync/apply`.** mTLS; one action per request. Idempotency by op id: the outcome is stored with the apply in one commit (WIP `SyncInboxEntry`, `Models/ReplicationDocuments.cs:121-135`, through `ReplacedSave.Alongside`), and a replay returns the stored outcome instead of a false Conflict. The save runs as `Module:{consumer}` **with attribute rights** (WIP `SparkSystemContext.IsModulePrincipal`; `Services/AttributeRights.cs`): an attribute the module may not write makes the action `Refused(unwritable: [...])` (`SparkReplacedSaves.cs:78-80`) instead of today's silent drop (F17). Answers: 200 `applied`, 409 `conflict(changed|deleted)`, 403/422 `refused(reason)`. No more 207 (`Endpoints/SyncApply.cs:171-173`).
- **Consumer outcome handling** (the dispatch recipient): `applied` → `Completed`. `conflict`/`refused` → `Refused` with its reason (WIP `ESyncActionStatus.Refused`), and the **initiator is notified**: `ISyncActionRefusalObserver` (WIP) plus, when `libs/notifications` is present, `ISparkNotifier.NotifyAsync` to the initiator (`libs/notifications/MintPlayer.Spark.Notifications/ISparkNotifier.cs:18-20`). Unreachable / 5xx → `RetryAfterException` on the same ladder (1 m … 2 h, 48 h) as pushes; dead-lettered → `Failed`, re-drivable with a `RetrySyncAction` custom action (F14). Today any 2xx is `Completed` (`Workers/SyncActionSubscriptionWorker.cs:75-85`) and retries end after about 5–8 minutes.
- **No echo.** The replica is never saved locally, so the owner's push of the applied state is the only change, and it reaches the requester like every other consumer. (The reference system needed a `Source` stamp to drop its own echo because it saved locally first.)
- **What the editor sees** (Q-W18): the save answers "sent to {owner}", the form shows a pending badge until the push lands; a conflict arrives as a notification with "reload and edit again".
- **Subscriptions.** `SyncActionSubscriptionWorker`, `SyncActionRetrySweeper` and `SparkSyncActions_ByStatus` are deleted. Replication then needs **no subscription of its own**: messaging's one is shared (`SparkMessagingOptions.cs:195-212`).


</details>

#### 4.2.6 Security (WX4)

- Every cross-module call uses module client certificates (WD1; `Authentication/ModuleCertificateAuthentication.cs:49`, `docs/guide-replication-mtls.md:21-38`).
- No consumer-authored script ever runs (re-render check, no override); reads are bounded by the `Replicate/{SourceCollection}` grant **and** attribute read rights, checked at register, at approval, and again at every deploy and reconcile (a revoked grant suspends and removes delivery).
- Writes respect attribute rights (F17). Destinations of ETL tasks and pushes come from the registry entry of the authenticated module (F19).
- **Registry trust root.** Any module can rewrite another's pinned thumbprint and URL in `SparkModules` (`libs/replication/MintPlayer.Spark.Replication.Abstractions/Models/ModuleInformation.cs:18,30`); WX4's operator-managed read-only registry mode stays in scope.
- Behind Traefik: the forwarded certificate plus `KnownProxies` (`Authentication/ModuleCertificateForwarding.cs:9-35`).

#### 4.2.7 Failure modes (W5 measures them)

| Failure | Outcome |
|---|---|
| Consumer down for hours | Pushes and registrations park on the ladder; the backlog drains on recovery; past 48 h they dead-letter and the next reconcile repairs. Replicas stay readable |
| Owner down | A save cannot reach the owner within 5 s: the edit is queued in the same commit ("saved, pending at {owner}"), retried on the ladder; the row shows the pending badge; after 48 h `GaveUp`, the initiator told (live + "My pending changes") and mailed, re-drivable with `RetrySyncAction` |
| Crash after commit, before the push | The after-commit work message was stored in the same commit and runs after the restart |
| Lost 200 on apply | Resend hits the change-vector guard (push) or the op-id record (write-back): no-op, same answer |
| Duplicate or reordered pushes | Change-vector guard plus tombstones |
| Stale replica edit | A listed property changed at the owner: `Conflict(changed)` naming it, nothing applied (WD19); within the save it is the 409 a person gets, when queued it ends `Conflict` and the initiator is told. A change of an unlisted property is not a conflict |
| Consumer upgraded with a new manifest | It re-registers (hash changed); pushes in the last approved shape keep landing (Q-W29); the new fields arrive with the initial reconcile after approval |
| Raw write on the owner | `MarkChanged`, else the reconcile |

**Cost:** no ETL licence, no new subscription, no cross-database certificate; it also works across RavenDB servers.

**Rejected** (research §5; still valid): a pull change feed (cursor gaps; revisions capped at 2 on Community); a subscription on the other app's database; SyncAction as the push; buying ETL. **Rejected 2026-10-20:** hand-written contract records (duplicate the replica class); a runtime-compiled projection (`EtlTransformGenerator @e0c542f9`); a consumer-authored script override.

**Fleet and HR** are migrated (WM1, 2026-10-20): `partial` replica classes `[Replicated(SourceCollection = …, SourceModule = …, WriteBack = true)]` (`apps/HR/HR.Library/Replicated/Car.cs`, `apps/Fleet/Fleet.Library/Replicated/{Person,Company}.cs`); the contract records and `Publish` are deleted; `Program.cs` calls `AddReplication()` with no code options; `appsettings.json` sets `Transport: Http` (Q-W24); grants stay `Replicate/Cars`, `Replicate/People`, `Replicate/Companies`; both bind `replication:approvers` to `Administrators` (both own something). The demos run Development, so they auto-approve; the ng-spark shell shows `<spark-pending-changes-link>` and `provideSparkReplication()`.

**§4.3 terminology.** The contract names in §4.3 (`fulfilment-orders`, …) become `[Replicated]` replica classes in `Fulfilment.Contracts` without `SourceModule` (the host names the source in `Spark:Replication:Sources`); "v1" becomes the class and target collection name. As built (WM2): see §4.3 "Contracts as replica classes".

**Reference data** (WD1): the reference-data app is an owner like any other; consumers declare replica classes.

<details>
<summary>Superseded 2026-10-20 (kept for the record)</summary>

- ~~Contracts declared in configuration, versioned, from several sources: `Publish<WarehouseShipment, ShipmentV1>("shipments", 1, …)`; consumer replica types bound to `(contract, versions)`; `Spark:Replication:Consume:{contract}:Sources`; grants `Replicate/{contract}`.~~ → WD13: the replica class is the declaration; several sources survive as `Spark:Replication:Sources:{TargetCollection}`; grants stay per source collection.
- ~~The owner generates the transform from the contract; the consumer sends only `(contract, versions)` (Q-W17).~~ → WD14: the consumer's generator emits manifest + script; the owner re-renders and approves (WD16).
- ~~An `IBeforeSave` increments `ReplicationVersion`; newer `replicationVersion` wins.~~ → WD18: the source change vector is the version; nothing is written to owner documents.
- ~~One messaging queue per consuming module.~~ → one `spark-replication` queue; queues must be declared in configuration (`MessageBus.cs:158-177`), and the change-vector guard makes ordering unnecessary.
- ~~The consumer runs the reconcile against an owner digest.~~ → the owner runs it, so repairs reuse the push path.
- ~~The SyncAction worker registers only when a replica allows write-back.~~ → the worker is removed; write-back runs on messaging.
- ~~WIP b4c218eb: `[Replica]`, `[ReplicationContract]`, `*V1` records in apps.~~ → rejected by the owner.
</details>

### 4.3 Shop ↔ warehouse: state both ways, one owner per fact (WD2)

Owner decision WD2:
- state replication both ways;
- one owner per fact;
- **no app writes the other's documents**.

Each side reacts to the other's replicas through `IReplicaChanged<T>`.

| Contract ~~(v1)~~ | Class = collection (WM2) | Owner | Consumer | Content |
|---|---|---|---|---|
| `fulfilment-orders` | `FulfilmentOrder` = `FulfilmentOrders` | shop | warehouse | • Id, unique across shops; owner; channel; order number, placed-at<br>• Lines (line id, SKU, quantity, name, required lot); ship-to address (`FulfilmentAddress`, plain text incl. service point)<br>• Carrier and service chosen at checkout<br>• `ShipNotBefore`, `ShipComplete`, `BackorderPolicy` (`Allow` \| `CancelRemainder`), priority, hold codes<br>• `CancellationRequested`, with an optional line subset |
| `catalog-items` | `FulfilmentCatalogItem` = `FulfilmentCatalogItems` | shop | warehouse | Owner, SKU, name, GTINs, variant, discontinued (WD9) |
| `expected-returns` | `FulfilmentExpectedReturn` = `FulfilmentExpectedReturns` | shop | warehouse | Return id and number, order and line references, quantities, a reason per line (GA8) |
| `expected-receipts` (Q-W9) | `FulfilmentExpectedReceipt` = `FulfilmentExpectedReceipts` | shop or ERP | warehouse | Owner, ASN reference, supplier, site code, expected day; lines (SKU, quantity, lot, expiry) |
| `fulfilment-status` | `FulfilmentOrderStatus` = `FulfilmentOrderStatuses` | warehouse | shop | • Id `{fulfilmentOrderId}/status`; owner<br>• Per order: `Accepted` \| `Rejected(reason)`, then picking, picked, packed, shipped (and `Cancelled`)<br>• Per line: shipped, backordered and cancelled quantities<br>• `Cancellation`: `Accepted` \| `Refused(reason)` |
| `shipments` | `FulfilmentShipment` = `FulfilmentShipments` | warehouse | shop | • Id `{fulfilmentOrderId}/shipments/{n}`; owner<br>• Lines with lots and serials<br>• Packages: carton, weight (g), dimensions (mm), SSCC, tracking number and URL, label reference<br>• Carrier, service, shipped-at, tracking state |
| `stock-levels` | `FulfilmentStockLevel` = `FulfilmentStockLevels` | warehouse | shop | Per owner and SKU: `OnHandSellable`, and per channel `ReservedOtherChannels` and `ShippedTotal`; version (§4.4) |
| `return-receipts` | `FulfilmentReturnReceipt` = `FulfilmentReturnReceipts` | warehouse | shop | Return reference (null: blind), order, owner, lines received, a disposition per line (+ override reason), received-at |

**Contracts as replica classes (WM2, 2026-10-20; supersedes "versioned contract records").** After WD13 a contract is not a record beside the replica: it **is** one `partial` class with `[Replicated(SourceCollection = …)]` and no `SourceModule`, in `MintPlayer.Spark.Fulfilment.Contracts` (`libs/fulfilment/contracts/`; files `ShopToWarehouse.cs`, `WarehouseToShop.cs`, `FulfilmentContracts.cs`).
- **Both sides store the same class.** The owner writes it as its own published document (projected from its internal state in the same session, e.g. the warehouse's `OutboundOrder` → `FulfilmentOrderStatus`), so the generated `SourceCollection` equals the generated `TargetCollection` (the class's plural, `FulfilmentCollections.*`), and the manifest's field paths exist on the source by construction. No `OriginalType` is needed and nothing can drift: renaming a member changes both sides and the hash. Internal documents stay private.
- **Binding.** Both apps reference the library, so each host sees all eight classes. The assembly is marked `[assembly: SparkReplicaContracts]` (new, `libs/replication/MintPlayer.Spark.Replication.Abstractions/SparkReplicaContractsAttribute.cs`): a class of such a library is consumed only when the host binds `Spark:Replication:Sources:{TargetCollection}` to one or more owner modules (`"FulfilmentOrders": [ "ShopA", "ShopB" ]`); unbound it is **inert** in that host — not a replica, not registered with an owner, writable as an ordinary document — so the owner's own contracts never trip "names no source module" (`ReplicaCatalog.Discover`). Outside a marked library the startup refusal stays.
- **Owner obligations.** Store the class unrenamed (a store convention that renames the collection breaks it), grant each consumer `Replicate/{collection}`, use natural ids unique across sources (`FulfilmentOrders/{shop}/{orderNumber}`), cumulative counters, forward-only states. `WriteBack` is off on all of them (WD2: no app writes the other's documents).
- **Versions.** A changed shape is a new class and collection (`FulfilmentShipmentV2`), beside the old one until every consumer moved.
- ⚠️ **Open (multi-shop warehouse):** replication sends every document of an approved collection to every approved consumer; it has no per-consumer row filter. A warehouse serving several shops would publish shop A's status, shipments, stock levels and return receipts to shop B. Each warehouse-owned contract carries `OwnerId` so a filter can key on it (Q-W23: "the link also filters which stock levels a module receives"); the filter itself is not built (owner-side manifest filter, or one collection per consumer). Single-shop hosts are unaffected.

Mapped to EDI X12 (research §6): fulfilment order ≈ 940, shipment ≈ 945, stock level ≈ 846, return ≈ 180.

**Derivation rules.**
- **Natural ids everywhere**, so a redelivery is a no-op:
  - `outbound/{fulfilmentOrderId}` for the warehouse's working document;
  - `{fulfilmentOrderId}/shipments/{n}` for shipments;
  - the op id for movements.
- **Idempotent and order-insensitive.** "Packed" arriving after "shipped" is a no-op. State machines refuse illegal transitions, such as picking after a cancellation.
- **Cumulative counters.** Counters cross the boundary as cumulative values, never as deltas (§4.4).

**Intake.**
- The warehouse creates its own `OutboundOrder`, then answers `Accepted` or `Rejected` with a reason: unknown SKU, owner not served (Q-W23), invalid address.
- The shop states intent (`ShipNotBefore`, `ShipComplete`, backorder policy, priority, hold). The warehouse decides capacity.
- Orders are routed to a site by configuration rules, or to the owner's default site (WD3).

**Cancellation handshake (WD2).** The shop sets `CancellationRequested` on its own order, and the warehouse answers in its own status. The rules per state are configurable (research §6):

| State | Rule |
|---|---|
| Not yet picked | Accept: release the allocations, void the tasks |
| Picked, not packed | Accept with a put-back task, or refuse (a switch; default: Q-W2) |
| Packed with a label | Accept only if the carrier label can be voided |
| Shipped | Refuse ("already picked up by the courier"). The shop turns it into a return |

Edits after acceptance:
- a change of lines or address is a cancellation plus a new order;
- an address change uses the same handshake, and is accepted only before a label exists.

**Partial shipments and backorders.**
- One order can produce several shipments.
- Unshipped quantity follows the shop's `BackorderPolicy`:
  - `Allow`: the quantity stays open and is re-allocated when stock arrives;
  - `CancelRemainder`: the quantity is reported cancelled, and the shop refunds it and lowers `sold` (rule 1 in §4.4).
- `ShipComplete=true` keeps an order unallocated until every line can be allocated.

**Returns (WD5, WD12).** The flow:
1. The shop creates the return (14-day withdrawal, commerce §4.7; round 5: also a legal-guarantee claim, a wrong item, transit damage or a seller-allowed return).
2. It reaches the warehouse as `expected-returns`, **with a return reason per line** (commerce GA8: `Withdrawal`, `NonConformity`, `WrongItem`, `DamagedInTransit`, `SellerAllowed`, `Other`). The reason sets the **default disposition**: `Withdrawal` and `SellerAllowed` → Restock after inspection; `NonConformity` and `DamagedInTransit` → Inspect then Quarantine (pending repair, replacement or return to vendor); `WrongItem` → Restock. Inspection can always override it, with a reason code.
3. The warehouse receives it, by scanning the return or order barcode, or blind.
4. The warehouse inspects each line and gives it a disposition: **Restock**, **Quarantine**, **Scrap** or **Return to vendor**.
5. The result goes back as `return-receipts`.

- **Refund timing:** for warehouse-backed stock, the refund waits for the warehouse's receipt by default. The host can switch to refund on request (WD12, commerce §4.9). *(Round 5, commerce GA1: for a withdrawal the refund may wait only until receipt **or the buyer's proof of dispatch, whichever is earlier** (CRD Art. 13(3)), so the shop may release it before `return-receipts` arrives. The quota rule below is unchanged: restock, not refund, raises it.)*
- **Quota:** restock raises `OnHandSellable`; a refund of shipped units returns no quota (commerce I4 exception).

**Genericity.**
- Fields stay generic: lines, quantities, addresses, shipments.
- A Spark app without the commerce packages drives the full cycle through the contracts alone (W8). That also covers commerce S20.
- Commerce S20's "status write-back lands on the owner" predates WD2. Status now comes back as the warehouse's own contract.

### 4.4 The stock feed to the shop

**The problem** (research §6). The shop already counts its own sold-but-unshipped orders and its holds (commerce I1: `sold + activeHolds ≤ capacity`). Two naive feeds both fail:

| Feed | Failure |
|---|---|
| "available = on hand − allocated" | The shop subtracts its own orders twice once the warehouse allocates them |
| Plain on-hand | Shipments and stock changes arriving in different orders open an oversell window |

**The contract.** Per owner, SKU and channel, the warehouse publishes:

```
OnHandSellable          physical stock in sellable status (definition: Q-W5)
ReservedOtherChannels   accepted, unshipped quantity of OTHER channels' orders
ShippedTotal            cumulative units shipped for THIS channel; monotonic
Version                 consumers ignore anything older than what they hold
```

The shop's capacity source (commerce §4.9) sets:

```
capacity  = OnHandSellable − ReservedOtherChannels − buffer + ShippedTotal
remaining = capacity − sold − activeHolds
          = OnHandSellable − ReservedOtherChannels − buffer − (sold − ShippedTotal) − activeHolds
```

- `sold − ShippedTotal` is exactly "sold for this channel and not yet shipped".
- At ship-confirm, `OnHandSellable` goes down and `ShippedTotal` goes up. Both live in **one document written in one transaction** (WI6).
- So a reordered, late or duplicated delivery cannot create oversell, and a stale snapshot is simply superseded by `Version`.

| Event | Effect on the shop's capacity |
|---|---|
| Receipt into sellable stock | Rises |
| Shipment | Neutral (on hand −n, shipped +n) |
| Return restocked | +1 per unit |
| Return scrapped | None |
| Shrinkage adjustment | Drops, possibly below `sold`. The shop stops selling, and the warehouse short-picks into the backorder flow |

**Where the guarantee holds.**
- **One channel:** zero oversell, **exactly**, for a SKU sold through one channel.
- **Several channels:** the error is bounded by other channels' orders accepted during the replication lag. The lever is the shop-side buffer per channel; allotments are Q-W4.
- **No acknowledgement handshake** is needed for the same-channel case (that alternative is in research §3.8). The warehouse never subtracts this channel's own allocations, so nothing is counted twice.

**The rules the guarantee needs:**
1. The shop lowers `sold` only when the warehouse **confirms** a cancellation or reports a cancelled remainder, never on the request.
2. `sold` and `ShippedTotal` share an epoch. At onboarding, units shipped before the integration go into `ShippedTotal` as a baseline.
3. Bundles map to component SKU quotas through the commerce quota multipliers (commerce §4.3).
4. A refund of shipped units returns no quota; a restock does (commerce I4 exception).

**Frequency.**
- Changes flush every 1–2 s as a dirty set (§4.2).
- A daily reconcile compares digests and alerts on drift.
- Checkout holds stay in the shop: the warehouse never allocates for baskets.
- Stock across several sites is Q-W3.

**Shape.** Initial; W1 and W2 validate it.
- One `StockLevel` document per (owner, item) holds `OnHandSellable` and, per channel, the open quantity and `ShippedTotal`.
- Every movement that changes these numbers updates the document in the same session as the balance and ledger writes.
- The contract publishes one triple per channel, filtered to the owners mapped to the consuming module (Q-W23).

### 4.5 Warehouse model (WD3, WD4, WD9)

These shapes are initial; W1, W3 and W4 validate or replace them.

| Document | Id (proposal) | Content | Notes |
|---|---|---|---|
| `WarehouseSite` | `warehouse-sites/{code}` | Name; address (reference data, commerce §4.18); IANA time zone; default carrier accounts; wave cut-offs | Per-site overrides of `Spark:Warehouse` |
| `WarehouseZone` | `{siteId}/zones/{code}` | Kind: pick zone (for zone picking), bulk, cold, quarantine… Mixing rules | WD5 |
| `WarehouseLocation` | `{siteId}/locations/{code}` | • Zone; parent with a materialized path; human code; barcode<br>• Check digits; walk sequence<br>• Type: Receiving, Storage, PickFace, Staging, Consolidation, PackStation, Quarantine, Returns<br>• Aisle, bay, level, position, side<br>• Capacity hint; fixed item with min/max; active flag; count class | Separate documents. Deactivated, never deleted, once it has ledger history |
| `WarehouseItem` | Immutable id; unique key (owner, SKU) | • Owner, SKU, replica link<br>• Dimensions, weight<br>• Packaging levels with quantities<br>• Storage requirements; tracking (none, or lot + expiry); minimum shelf life<br>• Removal rule; default and fixed locations<br>• **Round 5 (commerce GC9):** dangerous-goods data: UN number, class, packing group, lithium-battery kind (none, in equipment, with equipment, alone), limited-quantity flag | WD9; pre-items (Q-W12). Dangerous-goods items restrict carrier services at packing (§4.7) and may need a segregated zone (`WarehouseZone` mixing rules) |
| `CatalogItem` (replica) | Source id | SKU, GTINs, name, variant, discontinued | Read-only. Tombstoned while stock remains (WD9) |
| `BarcodeRegistration` | Per (code, item, owner) | Normalised code; type; packaging level and quantity; owner; item | WD8; one code may map to several items |
| `StockLot` | `{itemId}/lots/{lotCode}` | Lot code; expiry (`DateOnly`); status: Released, Quarantine, Recalled | WD4. **Recall trace (round 5, commerce GA7):** below |
| `StockBalance` | `{locationId}/{itemId}/{lotKey}` | Owner; `OnHand`, `Allocated` (base units); a copy of the expiry, for FEFO | WD4: one document per (location, SKU, lot) |
| `StockMovement` | The op id | From, to, item, lot, quantity, reason code, reference, owner, user, device, device time, server time | The append-only ledger, and the idempotency anchor |
| `StockLevel` | `{ownerId}/{itemId}` | `OnHandSellable`; per channel the open quantity and `ShippedTotal`; version | The feed (§4.4) |
| `OutboundOrder` | `outbound/{fulfilmentOrderId}` | Site; state; lines with allocations (balance, lot, quantity) and picked, packed, shipped, backordered and cancelled quantities | The warehouse's working state for a replica |
| `WarehouseTask` (one collection), `WarehouseTaskGroup` | — | • Task: one unit of directed work. `Kind` is Pick, PutBack, Putaway, Replenish, Count or Inspect, with a kind-specific payload. A pick task is one location visit, in walk-sequence order<br>• Group: a wave, a pick batch (with its picker and a lease, X12), or a count run<br>• Totes: container ids, unique while in use (X12 claims) | WD5. One collection for every kind keeps one index (index budget below) |
| `ConsolidationSlot` | `{siteId}/consolidation/{slot}` | Order; expected zone totes and lines; arrivals | Zone and batch picking |
| `InboundReceipt` | — | Expected or blind; lines with lot and expiry; discrepancies | WD5; its put-away work is Putaway tasks |
| `CycleCountPlan` | — | Class schedule. It generates Count tasks, which carry the blind results and variances | WD5 |
| `WarehouseShipment` | `{fulfilmentOrderId}/shipments/{n}` | Lines (lots, serials); packages; carrier and service; shipped-at | Immutable once shipped; published as `shipments` |
| `CartonType` | — | Dimensions, maximum weight, tare | Cartonisation |
| `ReturnReceipt` | `{returnId}/receipt` | Lines, inspection, disposition | Published as `return-receipts` |
| `ProductivityEvent` | — | Kind, user, task, quantities, timestamps | Q-W19 |
| `ReasonCode` | — | Adjustment and short-pick reasons | Editable by the host |

**Invariants.** Every test and spike checks these:
- **WI1** `OnHand ≥ 0` for every balance.
- **WI2** `0 ≤ Allocated ≤ OnHand` for every balance.
- **WI3** The ledger sums to the balances, per (location, item, lot).
- **WI4** An op id is applied at most once: the movement id is the op id.
- **WI5** Every owner-bearing document belongs to one owner, and no member of one owner ever reads another owner's document, in lists, pick lists, search or exports (WD3).
- **WI6** `OnHandSellable` and `ShippedTotal` of an item change in the same transaction.

**Recall trace (round 5, commerce GA7; GPSR Reg. 2023/988 recalls, confirm the duties on EUR-Lex, commerce D37).** Setting a lot to **Recalled** blocks it at once (no allocation, pick or ship; open allocations on it are re-allocated or short-picked) and enables a **forward trace**: lot → `StockMovement`s out of stock → `WarehouseShipment` lines (lots are recorded per shipment line at ship-confirm, §4.7) → fulfilment order → the shop's order and buyer contact. The warehouse holds no buyer contact beyond the ship-to address (§4.15), so the report it produces lists shipments and fulfilment-order ids, and the **shop** resolves them to buyers and sends the recall notice (a commerce mail template). The trace also lists stock still on hand per location and lots of the same item received in the same receipt. It is an owner-scoped report (WI5), exportable as CSV, and every run is audited. Items without lot tracking are traced by item and ship date range instead.

**Concurrency.**
- Moves, picks, put-aways and adjustments are domain services exposed as custom actions.
- Each writes in one session with `OptimisticConcurrencyMode.Writes` and retries on `ConcurrencyException` (`SparkQuotas.cs:46-77`). The session covers:
  - the source and target balances;
  - the movement;
  - the task state;
  - the stock level.
- On a cluster they use cluster-wide transactions (`UserStore.cs:203-209`).
- A balance created on first put-away uses commerce X3, an atomic natural-id create. Today creation is probe-then-store (commerce F6).

**Read-only stock.**
- Balances and the ledger cannot be edited in the UI.
- Corrections are adjustments with reason codes.
- The ledger has no edit or delete path.

**A ledger, not History.**
- The ledger replaces revisions, which Community caps at 2 per document.
- Raw writes aren't audit-stamped (`docs/guide-auditing.md:123-131`), so the user and the device are stamped explicitly.
- Retention is a host setting, never the expiry sweep. On Community that sweep runs at most every 36 h (`docs/guide-expiration.md:54-55`).

**Indexes never decide.**
- Plugin indexes ship through `AddIndexesFrom`.
- A map-reduce index sums the ledger, to reconcile the balances.
- Allocation takes its candidates from a query, then loads each balance and claims it under OCC. A stale index can hide a candidate, but it can never cause oversell.

**Owners (WD3).**
- Every stock, item, order, receipt, shipment and return document carries `IOrganizationOwned`, with the owner row policy.
- Raw session queries are not filtered (`IOrganizationOwned.cs:21-22`), so every library query filters explicitly.
- Tests assert WI5 everywhere.

**Units (WX8).**
- Quantities are stored in base units.
- Packaging levels convert at input and display, so scanning a case barcode books its pack quantity.
- Model types stop at `decimal` today (`SparkModelShape.cs:191-216`).

**Lots and expiry (WD4).**
- Expiry is a `DateOnly` (`SparkModelShape.cs:206`).
- FEFO sorts by expiry minus the item's minimum shelf life (Odoo's "removal date", research §1). Ties go to the lowest lot, then the earliest receipt.
- GS1 AI (10) and (17) are parsed at receiving.
- **Serials** are captured on the parcel at packing. They are not tracked as stock.

**Locations.**
- **Separate documents.** AsDetail rows are stored inside the parent (`docs/guide-asdetail-attributes.md:522-552`), and a site has 10⁴–10⁵ locations.
- **Routing by walk sequence.** WMSs sequence locations along a path instead of modelling the floor's geometry. The best routing heuristics come within about 5% of optimal, while S-shape routes run 7–33% longer (research §1).
- **Geometry stored anyway.** Aisle, bay, level, position and side are kept, so a host's `IPickRouter` can use them.
- **Check digits** are printed on the label, and asked for when a code is keyed in by hand.
- **Deletion.** A location with ledger history is deactivated, never deleted.
- **Picker.** The location picker becomes hierarchical (WX9). Today it is flat (`reference-query-tree-select-provider.ts:35-38`).

**Items (WD9).**
- **The shop owns sellable items.** The warehouse holds them as read-only `catalog-items` replicas.
- **The warehouse extends each item** with its own document: dimensions, weight, packaging levels and their barcodes, storage requirements, lot and expiry policy, default and fixed locations, min/max.
- **Identity** is (owner, SKU).
- **Unsold goods.** The warehouse can own items nobody sells.
- **Pre-items.** A pre-item created before launch is linked to the shop's item later, **without moving stock or splitting history**:
  - balances and ledger rows point at the item document's id, which never changes;
  - (owner, SKU) is a unique key on that document;
  - whether the SKU may change on link is Q-W12.
- **Discontinued products.** A product the shop discontinues leaves a **tombstoned** replica while stock remains.

**Bulk data.** CSV import and export for locations, items, barcodes, counts and expected receipts (WX11).

**Index budget.** ⚠️ **Corrected (2026-10-17):** RavenDB **lifted** the Community caps of 12 static and 24 auto indexes per database on **2024-05-22** ([discussion #18564](https://github.com/ravendb/ravendb/discussions/18564)); the buy page's footnotes are stale. Read `/license/status` to confirm. The budget below is kept as good practice (fewer indexes, less indexing work on 3 shared cores) and as the fallback if an old licence still carries a cap. ~~RavenDB Community allows at most **12 static and 24 auto indexes per database** (ravendb.net/buy).~~
- **The budget is shared.** The warehouse app's database also holds the indexes of Spark and of every plugin it composes.
- **Auto indexes are spent too.** RavenDB keeps roughly one auto index per queried collection, merging fields as queries need them. Every collection a page filters or sorts without a static index therefore spends one of the 24.
- **Today's composed static indexes:**
  - Messaging deploys 2: `SparkMessages_ByQueue`, `SparkMessages_ByPriority`;
  - Replication deploys 1: `SparkSyncActions_ByStatus`;
  - the IdentityProvider's 2 and Moderation's 4 are not part of a warehouse app.

The plan below is a proposal: one multi-map index per concern. W19 verifies it.

| Index | Kind | Library | Serves |
|---|---|---|---|
| `SparkMessages_ByQueue`, `SparkMessages_ByPriority` | map (existing) | Messaging | The message bus |
| `SparkSyncActions_ByStatus` | map (existing) | Replication | Write-back retries. Deployed only where write-back is used (WX3) |
| `Warehouse_Stock` | multi-map: `StockBalance` + `WarehouseLocation` | core | Allocation candidates, put-away (same lot, nearest empty), stock and location grids |
| `Warehouse_LedgerTotals` | map-reduce: `StockMovement` | core | WI3 reconciliation |
| `Warehouse_Work` | map: `WarehouseTask` (every kind, one collection) | core | My tasks, supervisor queues, live lists, productivity figures |
| `Warehouse_Catalog` | multi-map: `WarehouseItem` + `CatalogItem` replicas + `BarcodeRegistration` | core | Item search by SKU, name or GTIN; the registry grid |
| `Warehouse_Documents` | multi-map: outbound orders, receipts, shipments, returns | core; maps contributed by Inbound, Outbound and Packing (Q-W26) | Document grids by state, site, owner and date |
| `Printing_Jobs` | map | Printing | The agent's pull queue; printer queues |

**Totals:**

| | Static indexes |
|---|---|
| Existing, composed | 3 (2 without write-back) |
| Warehouse libraries | 5 |
| Printing | 1 |
| **Total** | **9** (8 without write-back), leaving 3–4 for the host's own indexes and commerce Shipping |
| If Q-W26 is rejected | `Warehouse_Documents` splits into one index each for Inbound, Outbound and Packing: **11**, leaving 1 |

**Read paths that need no index:**
- point loads;
- id-prefix loads: a barcode by its code, an order's shipments (`{fulfilmentOrderId}/shipments/`);
- unfiltered collection queries for small lists: sites, zones, carton types, reason codes, printers;
- reconciliation, which pages its source collection without an index (W5 and W19 confirm).

**Rules:**
- Library pages bind every filtered or sorted query to one of the static indexes above (query-declared index bindings, #279). They create no auto indexes, which leaves the 24 to the host's own pages.
- At startup, WX13 sums the static indexes the composed app deploys and warns when the total exceeds the licence's limit. A health check reports the auto-index count against its limit.

### 4.6 Barcodes: registry and resolution (WD8)

Owner (WD8): "make sure the vendor barcodes and warehouse barcodes cooperate nicely (when multiple articles are know for a specific barcode, the user should be able to select the correct one)".

**Registry.** A `BarcodeRegistration` holds:
- the code;
- its type: GTIN-13 or GTIN-14, UPC, supplier, internal;
- the packaging level (each, case, pallet) with its quantity;
- the owner;
- the item or variant.

One code may map to several items, because vendor codes collide across owners and suppliers.

**Resolution order.**
1. **GS1 parse.** Either an AI element string, recognised by its symbology identifier (`]C1`, `]d2`, `]Q3` or `]e0`) or by its FNC1/GS structure, or a GS1 Digital Link URI. The result can carry a GTIN, lot, expiry, serial and count. Sunrise 2027 moves retail to 2D codes, so Digital Link must parse (research §2).
2. **Own codes:** location codes (with check digits), SSCCs (AI 00), totes and other containers, task and return labels.
3. **Item lookup** in the registry. GTINs are normalised to 14 digits.

**Results.**
- **One match:** proceed.
- **Several matches:** the **user chooses**, from a list narrowed by context:
  - on a task, the expected item is pre-selected;
  - other owners' items are filtered out.
- **No match:** "unknown barcode", with a *link to item* action for users who hold that right.
- **A case or pallet code** counts its packaging quantity.

**Where it runs.** The same pipeline runs in two places:
- in the browser, for instant feedback, with check digits validated before any server call;
- on the server, which is authoritative.

Steps are pluggable (`IBarcodeResolutionStep`).

### 4.7 Processes (WD5)

Owner decision WD5: research option A, **plus zone picking**. The owner chose this beyond the recommendation.

**Receiving and put-away.**
- **Start:** from an expected receipt (ASN; source: Q-W9), or blind.
- **Scans** parse GS1 (GTIN, lot, expiry, count). Case codes book their pack quantities. Over, short and damaged quantities are recorded.
- **Put-away rules,** in order (custom rules plug in through `IPutawayRule`):
  1. the item's **fixed** location;
  2. a location already holding the **same lot**;
  3. the **nearest empty** location by walk sequence.
- Put-away tasks confirm by scanning the location.
- Online only (WD6).

**Stock control.**
- The append-only ledger and the balances (§4.5).
- Reason-coded adjustments.
- Moves inside a site: scan from, scan to.
- **Min/max top-up:**
  - fixed pick faces carry a min and a max;
  - a daily sweep and every short pick create replenishment tasks from reserve, choosing lots by FEFO;
  - only fixed pick faces are replenished. D365 behaves the same way (research §1).

**Release.**
- Immediate, or **waves** run by cron at carrier cut-offs. Cut-off times are set per site (Q-W1).
- Manual holds and releases.
- The shop's holds (`ShipNotBefore`, hold flags) are respected.

**Allocation (WD4).**
- FIFO, FEFO or closest location, chosen per owner and per item, with a global default.
- Custom rules implement `IAllocationRule`.
- Allocation raises `Allocated` on the chosen balances in the same session as the order's allocation rows.

**Pick work.**

| Strategy | How it works | Fits |
|---|---|---|
| Discrete | One order per trip | Large orders |
| Batch | Several orders picked by article, then sorted at a put wall | Single-line and small orders |
| Cluster | One tote per order on a cart, sorted while picking | Many small orders |
| Zone | Pickers per zone. An order is split across zones, then consolidated | Many small orders over a large area |

- A **wave** is a release group with a grouping rule (carrier, cut-off, zone, category). It can feed any strategy.
- Tasks are sequenced by walk sequence; for zone picking, per zone.

**Scan-to-confirm.** The picker scans, in order:
1. the location, or keys its check digits;
2. the item, resolved through the barcode pipeline with the expected item pre-selected;
3. the **lot**, checked against the allocated lot (WD4). Lot substitution is Q-W6;
4. the quantity, per scan or prompted. Case codes count their packs;
5. the tote, when cluster picking.

A picker holds a batch under a lease (commerce X12). A supervisor can take the batch over when the lease lapses.

**Short picks and backorders.**
- A short pick:
  - zeroes the location's remaining balance, with a reason;
  - **re-allocates** from other locations;
  - backorders what remains, under the shop's policy;
  - optionally triggers a cycle count and a replenishment.
- Backordered lines are re-allocated when stock arrives.

**Consolidation (zone and batch picking).**
- Two forms:
  - a **put wall**, with one slot per order. The screen shows the slot, since pick-to-light is out of scope;
  - a **consolidation station**, where zone totes are merged per order.
- An order is released to packing only when all its zone totes or lines have arrived, unless the order allows partial shipment.
- A cancellation creates put-back tasks for every zone tote.
- W4 proves the bookkeeping.

**Packing.**
1. Scan the tote or order.
2. Verify lines by scan: every unit, every line, or none.
3. Cartonise: a manual carton choice, or a rule-based volumetric suggestion (the smallest carton type whose volume and weight limits fit). 3D packing is not built in (Q-W22).
4. Weigh: manual entry, or a scale through the agent (WD7).
5. Capture serials on the parcel (WD4).
6. Create the carrier label through `Commerce.Shipping` (§4.12) and print it (§4.11). Print the packing slip. *(Round 5: only carrier services that accept the parcel's dangerous-goods classes are offered, and the label carries the marks the carrier API takes (commerce GC9). Shipments leaving the EU customs territory get their CN22/CN23 or commercial invoice through the same adapter (commerce GB6). A return label can be printed into the parcel when the seller offers one (commerce GB5).)*
7. Optionally allocate an SSCC per carton, when the host configures a GS1 company prefix.
8. Ship-confirm: the stock leaves (on hand down, `ShippedTotal` up, WI6), the shipment is created, and the status says shipped. Whether a separate handover scan comes first is Q-W10. *(Round 5: each shipment line records the **lots** shipped, which the recall trace (§4.5, commerce GA7) follows.)*

**Counting (the Counting package).**
- **Count plans** per location class: **A every 30 days, B every 60, C every 120**, spread evenly (WD5). The figures come from a Dynamics GP example (research §1). Class assignment is Q-W8. Spot counts can be made on demand.
- **Blind counts:**
  - the expected quantity is never shown;
  - a count covers a whole location;
  - the location is held under a claim while it is counted (commerce X12).
- **Variances:** those within tolerance post as adjustments; the rest go to supervisor review.
- Online only (WD6).

**Returns.** Receipt, inspection and disposition (§4.3). Quarantine and returns locations hold stock outside `OnHandSellable`.

**Productivity events.**
- Events: task started and completed, line confirmed, short pick, scan mismatch, packed, shipped.
- They feed these metrics on the Dashboards plugin:
  - lines and units per hour, per user;
  - the pick exception rate;
  - release-to-ship cycle time;
  - dock-to-stock time;
  - count accuracy.
- Handling them as employee data is Q-W19.

### 4.8 Offline (WD6)

Owner decision WD6: online first, plus a disconnect queue for **assigned** tasks.

**Capture.**
- Assigned tasks are cached on the device.
- While offline, each scan or confirmation is stored in IndexedDB as a command, with the device time and a client-generated **op id**.

**Replay.**
- The queue replays in order on the standard `online` event, and on a manual "sync now".
- Nothing relies on Background Sync, which only Chromium has (K11).
- Proposal: a successful request also triggers a replay, because `online` can fire behind a captive portal.

**Applying on the server.**
- The server applies each op id exactly once (WI4).
- A command that would break an invariant is never forced. Examples: the location was emptied meanwhile, the task was reassigned or cancelled, the lot was recalled.
- Instead it becomes a **picker exception**, with a put-back or recount flow, and lands in the supervisor's queue.

**Limits.**
- **No new work offline.**
- Receiving and counting are online only. A count is an absolute value that conflicts with concurrent movements (research §2).
- The allocation reserved the stock before the device went offline. That reservation is the escrow that keeps offline picks safe (research §2).

**Device storage.**
- After a configurable time offline, the app warns. Safari deletes script-written storage after 7 days without interaction.
- The installed app asks for persistent storage (`navigator.storage.persist()`).
- The device time is recorded but never used for ordering. The server's receipt time is recorded beside it.
- Sign-out clears the cached tasks and the unsent queue, after a warning (WD10b). Idle sign-out with an unsent queue is Q-W13.

**Fit with the existing PWA.**
- The queue lives in IndexedDB, not in the service worker's data groups. `tools/verify-ngsw-config.test.mjs:90-96` forbids data groups for `/spark`, and it stays green.
- `@mintplayer/ng-spark/offline` and `/scanning` are shared with the commerce ticket scanner (commerce §4.11).

### 4.9 Devices and scanning (WD7, K11)

**Scan sources.**
- **Keyboard wedge.** Zebra and Honeywell handhelds in Chrome, and desktop USB or Bluetooth HID scanners.
  - Zebra DataWedge in string mode commits characters as text, so the device's keyboard layout should not matter. W9 confirms it.
  - A documented DataWedge profile ships with the plugin: Enter suffix, symbology identifiers on, GS handling.
  - DataWedge's intent output cannot reach a web page, so it is not used.
- **Camera.**
  - `BarcodeDetector` where the browser has it: Chrome on Android, macOS and ChromeOS only. Not Windows, Linux or Firefox; behind a flag in Safari.
  - Otherwise a **same-shape WASM ponyfill**: `barcode-detector` (MIT, ZXing-C++).
  - The ponyfill fetches its `.wasm` from jsDelivr by default, so the build serves it from the app instead (CSP, offline).

**Capture rules** (research §2):
- one focused capture element with `inputmode="none"`, so no soft keyboard pops up;
- a terminator plus inter-key timing tells a scan from typing; manual entry always works;
- symbology identifiers are parsed when the scanner sends them;
- check digits (GTIN, SSCC, location) are validated before any server call.

**AZERTY.** A US-layout HID scanner on a Belgian host types `&é"'(§è!çà` for the digits and swaps Q↔A and W↔Z. Scanners send physical key codes, and `KeyboardEvent.code` ignores the layout. The scan layer:
- **detects** the pattern and warns;
- decodes from `event.code` where possible;
- links a setup guide telling staff to set the scanner to the Belgian layout.

W10 measures it.

**Around the scan.**
- Screen Wake Lock while picking.
- Proposal: audible and vibration feedback on success and on error; vibration where the device supports it.
- Scales: manual entry, or through the agent (WD7).
- **No Web Serial or WebUSB** (K11, WD7). They are Chromium-only drafts, not W3C standards.
- Each feature gets a row in a browser-support matrix in the guide (K11).

**Live tasks.**
- Task lists use `/spark/live`. Assignments reach one device through commerce X2's private stream.
- Personal accounts (WD10) avoid the 5-sockets-per-user cap that shared accounts would hit (`docs/guide-live-updates.md:58`).
- W14 measures the cost at 50 handhelds.

### 4.10 Sign-in on shared and personal devices (WD10, WD10b)

- **Full personal sign-in on every device, each shift.**
  - No new credential scheme and no device enrolment.
  - The owner chose this over enrolled devices with badge and PIN switching (WD10), so the research's WX10 is dropped.
- **Standard APIs soften the friction** (K11):
  - **passkeys** where the device has an authenticator (`PasskeySignIn.cs:25`);
  - **WebAuthn cross-device (hybrid, QR) sign-in** with the operator's own phone, on shared handhelds and pack stations (WD10b);
  - **idle auto sign-out**, driven by Page Visibility and activity. Spark has none today, so WX15 adds it: `libs/authorization` and `ng-spark` contain no idle timeout, `ExpireTimeSpan` or sliding expiration, and their `visibilitychange` handlers serve other purposes (searched 2026-10-17).
- **BYOD phones** are supported (WD10b):
  - as passkey authenticators;
  - as the scanning device itself: camera scanning, installed PWA.
- **Data minimisation (WD10b):**
  - sign-out clears the cached tasks and the unsent offline queue, after a warning;
  - no customer address is kept beyond the current task. Pick tasks carry none, and the pack station loads only the address of the parcel in hand.
- W18 measures sign-in on real handhelds.

### 4.11 Printing and labels (WD7; shared with commerce Q-O9)

**Jobs.**
- A print job is one of:
  - a template plus data;
  - carrier bytes with their media type: PDF, or ZPL at a stated dpi.
- A job is routed to a printer, and each printer names its adapter.
- Job ids make delivery idempotent. States: queued, delivered, printed, failed.
- A reprint is an explicit new job.

**Adapters (K7, WD7).**

| Adapter | How | Notes |
|---|---|---|
| PDF print dialog (default, built in) | The station's browser opens an A6/4×6 or A4 PDF and calls `window.print()` | Zero install, always works, shows the dialog |
| On-site agent | `MintPlayer.Spark.Printing.Agent` connects **out** to the app, pulls jobs, and writes ZPL or raw data to LAN printers on TCP 9100 | A second deployable: install, update, device authentication, monitoring (Q-W14). Works with a cloud-hosted app and needs no inbound firewall rule |
| QZ Tray | The page talks to the local QZ Tray over a WebSocket | Silent printing needs signed messages: a signing endpoint holding the host's certificate, kept in user-secrets or configuration and never in the repo. Licence: LGPL-2.1 |
| Zebra Browser Print | The page talks to Zebra's local service | USB and network on Windows and macOS; network and Bluetooth on Android. Library licence: Q-W15 |

**Delivery guarantees.**
- **PDF fallback.** When the agent is down, a job that isn't delivered in time is offered at the station as a PDF (WD7).
- **Exactly once on retries** (WD7):
  - the agent keeps a journal of printed job ids and acknowledges each one;
  - the server marks a job printed on acknowledgement;
  - W16 tests this under faults.
- **Why the agent and the bridges exist** (research §2): a browser cannot open a raw socket, and a cloud server cannot reach a LAN printer.

**Rendering (WX6, shared with commerce).**
- **Barcodes:** Code 128, GS1-128, EAN-13, DataMatrix and QR. Today Spark renders only QR, inside Authorization, and only `AddAuthentication` registers it (`libs/authorization/MintPlayer.Spark.Authorization/Identity/SparkQrCodeRenderer.cs:17-44`; `Extensions/SparkAuthenticationExtensions.cs:91`).
- **Templates:** ZPL for thermal printers (203 and 300 dpi), and PDF.
- **A PDF rendering seam.** W17 chooses the library, shared with commerce Q-O9. Candidates:
  - QuestPDF (its licence has revenue limits);
  - PdfSharp/MigraDoc (MIT);
  - headless Chromium, which SeoTester uses (`apps/SeoTester/SeoTester/Reports/ReportPdf.cs:51,124`).
- **The label set:**
  - locations (code, check digits, barcode);
  - items and cases;
  - totes;
  - SSCC logistic labels: the SSCC as GS1-128 is the GS1 Logistic Label's only mandatory element;
  - packing slips;
  - paper pick lists, as a fallback.
- **Tests** compare generated ZPL text and rendered PDFs offline. Labelary (free tier: 3 requests/s, 5,000/day) is only for manual previews during development, never in CI.
- **Dates on labels:** expiry is a `DateOnly`. Timestamps go through `ISparkDateTimeFormatter` with the recipient's locale (commerce X14, X16).

### 4.12 Carriers (WD8)

**Whoever packs ships.**
- At the pack station the warehouse creates the label through the commerce carrier adapters (Sendcloud, bpost, PostNL, test; commerce D14).
- The warehouse receives the tracking webhooks (commerce X5), and the shop learns about them through the `shipments` replica.
- A shop without a warehouse app uses the same adapters itself.

**Accounts.**
- A carrier account per owner, or the site's default account.
- Credentials live in user-secrets or configuration.

**Ownership switch mid-stream.**
- Parcels the shop labelled keep reporting their tracking to the shop.
- Each app registers its own carrier integration and webhook, so a parcel's tracking stays with the app that created its label.

**Formats.**
- An adapter returns `(bytes, mediaType)`: PDF, or ZPL at a stated dpi.
- ZPL goes to thermal printers (through the agent or a bridge); PDF goes to the PDF path.
- Sendcloud v3 returns PDF, ZPL or PNG (research §2). The bpost formats are unverified; commerce S13 records them.

**Label void.** Voiding a label is an adapter capability. Cancelling a packed order depends on it.

**Webhooks.** Tracking webhooks are at-least-once and unordered. They are deduplicated on the provider's message id and sorted by event time (research §6).

### 4.13 Configuration and startup validation

Names and defaults are proposals unless the last column names a decision. Defaults the owner hasn't set are Q-W25.

| Setting | Values (proposed default first) | Decided by |
|---|---|---|
| `Spark:Replication:Transport` | `Auto` \| `Http` \| `Etl` | WD1 |
| ~~`Spark:Replication:Consume:{contract}:Sources`, `:Versions`~~ (superseded by WD13) | ~~Module names; versions~~ | ~~WD1~~ |
| `Spark:Replication:Sources:{TargetCollection}` | Owner module name(s) for a replica class that leaves `SourceModule` out (generic libraries) | WD13 |
| `Spark:Replication:Reconcile` | A 6-field cron, run by the **owner** (HTTP path only); default hourly | WD1, WD17 |
| `Spark:Messaging:Queues:spark-replication` | Push and write-back budget; library default 1 m / 5 m / 30 m / 2 h ladder, 48 h expiry | WD17 |
| `Spark:Warehouse:Routing` | Rules (owner, channel, ship-to country → site); otherwise the owner's default site | WD3 |
| `Spark:Warehouse:Release:Mode` | `Immediate` \| `Waves` \| `Manual`; cut-offs per site | WD5, Q-W1 |
| `Spark:Warehouse:Allocation:Rule` | `Fifo` \| `Fefo` \| `ClosestLocation` \| custom; per owner and per item | WD4 |
| `Spark:Warehouse:Picking:Strategies` | `Discrete`, `Batch`, `Cluster`, `Zone` | WD5 |
| `Spark:Warehouse:Picking:Confirm` | `ScanLocationAndItem` \| `ScanItem` \| `Manual`; lot check on | WD4, WD5 |
| `Spark:Warehouse:ShortPick` | `ReallocateThenBackorder` \| `Backorder` \| `CancelRemainder` \| `HoldOrder`; `TriggerCycleCount` | WD5 |
| `Spark:Warehouse:Cancellation:AfterPick` | `AcceptWithPutBack` \| `Refuse` | Q-W2 |
| `Spark:Warehouse:Packing` | `Verify`: `ScanEveryUnit` \| `ScanEveryLine` \| `None`<br>`Cartonization`: `Manual` \| `Volumetric`<br>`Sscc`: `Off` \| a company prefix | WD5 |
| `Spark:Warehouse:Counting` | Blind; ABC 30/60/120 days; tolerance → review | WD5 |
| `Spark:Warehouse:Replenishment` | `MinMax` (daily + on short pick) \| `Off` | WD5 |
| `Spark:Warehouse:Offline` | `AssignedTasksOnly` \| `Off`; `WarnAfter` | WD6, Q-W25 |
| `Spark:Warehouse:StockFeed` | Flush interval; daily reconcile | WD2, Q-W25 |
| `Spark:Printing:Printers:{name}` | Adapter: `Pdf` \| `Agent` \| `QzTray` \| `ZebraBrowserPrint`<br>Format: `Zpl203` \| `Zpl300` \| `PdfA6` | WD7 |
| Idle sign-out | A duration | WD10, Q-W25 |
| Refund gate for returns | Lives in commerce (§4.9) | WD12 |

**Precedence.**
- Configuration beats code, as in commerce §4.2 (Moderation's order).
- Proposal: per-site values override on the site document.

**Startup refuses:**
- `Transport=Etl` on a licence without ETL (F18);
- a consumed contract without sources;
- SSCC switched on without a company prefix;
- a printer whose adapter package isn't registered.

**Startup warns** when `Auto` resolves to ETL: "production on Community uses HTTP: test with `Transport=Http`".

### 4.14 Dates and times (K10)

- **Expiry and production dates** are `DateOnly`: no zone at all.
- **Timestamps** (movements, events, shipments) are `DateTimeOffset`. They render through `<spark-date-time>` in the viewer's zone (commerce X16, D33).
- **Wave cut-offs and count schedules are wall-clock times at a site.**
  - The site carries an IANA zone, from reference data, as venues do.
  - Spark Cron evaluates its expressions in UTC through NCrontab (`libs/cron/MintPlayer.Spark.Cron/SparkCronScheduler.cs:76,101-102,209`). A 16:00 cut-off would therefore drift by an hour at every DST change.
  - K10 keeps the conversion out of the plugin: **WX14** adds a core zone-aware schedule (Q-W1).
- **Day boundaries** for per-site metrics come from the same core built-in.
- **Offline commands** carry the device time. It is recorded but never used for ordering.
- **Labels and packing slips** format through `ISparkDateTimeFormatter` with the recipient's locale (commerce X14).
- **Banned symbols.** The new projects fall under K10's banned-symbols enforcement. Commerce M1 extends it beyond test projects; today it covers only `*.Tests` projects (`Directory.Build.targets:235-241`).

### 4.15 Security, privacy, audit

- **Owner isolation** (WD3, WI5) is asserted in tests everywhere, including pick lists and exports.
- **Stock is read-only:**
  - adjustments need a right and a reason;
  - out-of-tolerance counts need a supervisor;
  - the ledger has no edit or delete path.
- **Module calls:** certificates, contract grants, attribute rights, no consumer scripts (§4.2; F16, F17).
- **GDPR.**
  - Ship-to addresses on fulfilment-order replicas and on shipments are personal data.
  - A replica follows its producer: an anonymised order replicates as anonymised.
  - Warehouse documents holding addresses are anonymised after a retention period set by the host, through `ISparkPersonalDataContributor<>` and `ISparkAccountDeletionHandler<>` (commerce default 7).
  - Employee data is Q-W19.
- **BYOD minimisation** (WD10b, §4.10).
- **No string-spliced RQL.** Location codes, SKUs and scanned barcodes are untrusted input. Values go through query parameters, identifiers through `RqlIdentifier` (repo rule).
- **Agent least privilege.** An agent fetches and acknowledges only its own printers' jobs, and its credential can be revoked (Q-W14).
- **Secrets.** Carrier credentials, the QZ Tray signing certificate and agent credentials live in user-secrets or configuration. They are never printed and never put in fixtures.
- **MCP:** Q-W20.

### 4.16 Demo app (WD11)

**The app.**
- `apps/Warehouse`, with the projects `Warehouse`, `Warehouse.Library` and `Warehouse.Tests` (research §3.10).
- **Ports: https 5019 / http 5020.** They are free:
  - existing launch settings use 5003–5014 and 5200/5201;
  - commerce takes 5015/5016;
  - the reference-data research proposes 5017/5018.
- Database `SparkWarehouse`, cookie `.SparkAuth.Warehouse`, module name `Warehouse`.

**Local only (WD11).**
- It pairs with the commerce demo on `localhost:8080`, sharing a `SparkModules` database the way Fleet and HR do.
- It consumes `fulfilment-orders`, `catalog-items` and `expected-returns`.
- It publishes `fulfilment-status`, `shipments`, `stock-levels` and `return-receipts`.
- By default it uses the test carrier and PDF printing; the agent is optional.
- The seed data contains no personal data.

**New-app checklist** (commerce research §10):
- workspaces and the slnx;
- the `pull-request.yml` build and verify loops;
- the `verify-ngsw-config` app list (`tools/verify-ngsw-config.test.mjs:64-69`);
- `E2E_APPS` in `tools/test-local.mjs` (`:33`);
- `regenerate.yml`, route snapshots, `modelHashes.json`, `securityPosture.txt`;
- the `test.yml` matrix.

**Two-host E2E.**
- What exists: the embedded RavenDB server is one per test process (`libs/testing/MintPlayer.Spark.Testing/SparkEmbeddedServer.cs:6-24`), so the two databases already share it.
- What is missing:
  - one `SparkModules` database shared by both hosts. Today each host gets its own (`tests/MintPlayer.Spark.E2E.Tests/_Infrastructure/FleetTestHost.cs:43`);
  - the generalised host. Multi-host plan items H1, H2 and R2 are not started (`docs/multi-host-e2e-plan.md:12-26`).
- Waits poll point-loads under a failure bound.
- The missing end-to-end mTLS test is added (`tests/MintPlayer.Spark.E2E.Tests/Security/CrossModuleSyncTests.cs:24-31`).

---

## 5. Framework additions (Spark core and existing libraries, same PR)

> **Source-generator programme (commerce X20, K12, accepted 2026-10-17)** applies here:
> - the **shared discovery generator** registers `IReplicaChanged<T>` handlers, warehouse row policies and account hooks;
> - **GS1 tables** come from one `tools/` script emitting C# (`MintPlayer.Spark.Gs1`) and TypeScript (`ng-spark/scanning`), as Q-W16 decided. The **GS1 core** (element strings, Digital Link, linters, check digits) is written once in **MintPlayer.Polyglot** `.pg`. That holds only if a Polyglot release fixing [MintPlayer.Polyglot#73](https://github.com/MintPlayer/MintPlayer.Polyglot/issues/73) ships before WM2; otherwise both parsers are hand-written against the Syntax Engine vectors (owner decision, 2026-10-17). Polyglot's C# stays internal behind a hand-written facade, and its types never cross the wire. The CLI comes from the `MintPlayer.Polyglot.MSBuild` NuGet restore;
> - ~~**replication contracts** generate their ETL script at runtime, with complex projections refused at startup (Q-W17);~~ **Superseded by WD14 (round 3):** the `[Replicated]` replica class gets its manifest, script and hash from `MintPlayer.Spark.LibraryGenerators` at build time (SPARK065–SPARK068);
> - **TypeScript contracts** for the `scanning`, `offline`, `printing` and `warehouse` entry points come from the C#→TS tool;
> - **library-declared indexes** are emitted into the app (X19, which supersedes WX16's runtime mechanism);
> - `Quantity` companions come from the extended `GenerateIndexGenerator`.

| # | Addition | Why | Evidence of the gap | Shared with commerce? |
|---|---|---|---|---|
| WX1 | **The replication transport** (rewritten by WD13–WD18, §4.2):<br>• generated manifest/script/hash per `[Replicated]` class; registration with hashing on both sides;<br>• the owner approval gate (`SparkReplicationSubscription`, Development-only auto-approval);<br>• ETL by licence, else owner push over messaging with a change-vector-guarded apply, tombstones, `IReplicaChanged<T>` and an owner-run reconcile | WD1, WD13–WD17 | Community has no ETL (`docs/endpoints_generator_adoption_plan.md:219`); `EtlTaskManager.cs:92` | Yes: the fulfilment contract (commerce §4.9, S20) and reference data |
| WX2 | ~~**Contracts:** declared in configuration, versioned, from several sources~~ **Superseded by WD13:** the replica class is the declaration; several sources through `Spark:Replication:Sources`; the consumer registers with its sources; write-back routed per document to its source | WD1, WD13 | `ReplicatedAttribute.cs:9,16,34`: `AllowMultiple = false`, a required compile-time `SourceModule`, a required `EtlScript` | Yes |
| WX3 | **SyncAction hardening:**<br>• a request instead of a local save;<br>• dispatch inside the edit's commit;<br>• per-action outcomes;<br>• a configurable budget, with re-drive;<br>• the source change vector as `BaseEtag` (WD18), one pending action per document, op-id idempotency;<br>• structured nested values;<br>• ~~the worker registered only when used~~ the subscription worker and sweeper removed: dispatch runs on messaging | WD1 refined; WD18; F12–F15, F18 | `SyncApply.cs:149-161,171-173`; `SyncActionSubscriptionWorker.cs:20,75-85`; `SyncActionRetrySweeper.cs:77-79,85-89`; `SyncActionInterceptor.cs:149-164,170-185`; `SparkReplicationExtensions.cs:49` | Yes |
| WX4 | **Replication security:**<br>• no consumer scripts;<br>• destinations taken from the registry, never from the caller;<br>• subscription and ETL-task removal;<br>• attribute rights for module principals;<br>• an operator-managed read-only registry | F16–F19; the registry trust root | `EtlTaskManager.cs:52-60,65-70`; `EtlDeploy.cs:97-111`; `SparkReplicationExtensions.cs:135-147`; `docs/guide-authorization.md:122`; `ModuleInformation.cs:18,30` | Yes |
| WX5 | **Scanning** (`ng-spark/scanning`):<br>• wedge capture;<br>• camera: native `BarcodeDetector` plus a same-shape, self-hosted WASM ponyfill;<br>• GS1 parsing from the Syntax Engine's dictionary (Q-W16);<br>• AZERTY detection; scan-driven steps.<br>Server twin: `MintPlayer.Spark.Gs1` | WD7, WD8, K11 | No scanning code in `libs`, `apps` or `tools`, and none in ng-bootstrap 22.25.0 (research §3) | Yes: the scanner PWA (commerce §4.11) |
| WX6 | **Document and label rendering:**<br>• 1D and 2D barcodes;<br>• ZPL and PDF templates;<br>• a PDF seam (library per W17);<br>• print-delivery adapters | WD7; commerce Q-O9 | QR only, inside Authorization (`SparkQrCodeRenderer.cs:17-44`); no PDF library in `libs/` (SeoTester uses Chromium, `ReportPdf.cs:51,124`) | Yes: invoices, tickets, packing slips |
| WX7 | **Offline command queue** (`ng-spark/offline`) | WD6 | The PWA caches only the app shell (`docs/guide-pwa.md:33-34`, enforced by `tools/verify-ngsw-config.test.mjs:90-96`) | Yes: offline ticket scanning (commerce S9) |
| WX8 | **`Quantity`** (amount + unit) with packaging conversions | WD5; WD8 (case barcodes) | Model types stop at `decimal` (`SparkModelShape.cs:191-216`) | Builds on X4 |
| WX9 | **Hierarchical reference picker** plus materialized-path helpers | Locations | The picker is flat (`reference-query-tree-select-provider.ts:35-38`) | Venues and blocks could use it |
| ~~WX10~~ | ~~Device enrolment and badge/PIN user switching~~ | **Rejected by WD10** (full personal sign-in) | — | — |
| WX11 | **Generic CSV import and export** | Locations, items, barcodes, counts, expected receipts | None in `libs/` (research §3) | Yes: the seat CSV (commerce D32) |
| WX12 | **Carrier adapters without a Checkout dependency** | WD8 (the pack station) | **Applied** to the commerce package map (commerce §4.1) | Yes |
| WX13 | **Licence-capability detection** (`ISparkLicenseCapabilities`): reads the server's licence at startup (ETL; subscription and index limits). Startup warns when the composed app's static indexes exceed the limit; a health check reports auto indexes against theirs | WD1 refined (the transport follows the capability); F18; the index budget (§4.5) | ETL deployment fails at run time with a generic 500 (`EtlDeploy.cs:113-115`). Index caps: listed on ravendb.net/buy but lifted for Community on 2024-05-22 (#18564), so they are read from `/license/status`, never assumed | Yes: commerce S18 |
| WX14 | **Zone-aware schedules:** a cron expression evaluated on a site's local wall clock (IANA zone; DST gaps and folds defined) | Wave cut-offs; K10 | Cron evaluates in UTC (`SparkCronScheduler.cs:76,101-102,209`) | No |
| WX15 | **Idle sign-out or lock** for shared devices (Page Visibility plus activity; configurable) | WD10 | No idle timeout, `ExpireTimeSpan` or sliding expiration in `libs/authorization` or `ng-spark` (searched 2026-10-17) | Yes: the scanner PWA |
| WX16 | **Composable multi-map indexes:** packages contribute maps to one named index. ~~The core assembles them into one multi-map definition at startup~~ **Superseded by reference-data Q-R6 / commerce X19 (owner, Round 2):** packages *declare* their index definitions and the app's generator **emits** one multi-map index class into the host application at build time, which keeps the owner rule that index classes live in the app | The Community index budget (§4.5) | A C# multi-map index class can only map the types it references, and core can't reference Inbound, Outbound or Packing. (The Community 12-static-index cap was lifted on 2024-05-22; one composed index remains the cleaner design) | Possibly: every plugin family shares its app's budget |
| (deps) | Commerce additions this PRD uses:<br>• X2: private stream;<br>• X3: atomic natural-id create;<br>• X4: `Money` → `Quantity`;<br>• X5: Webhooks, for carrier tracking;<br>• X7: `ISaveReplacement`, for the write-back request;<br>• X12: claims (batch leases, tote uniqueness, count locks);<br>• X14, X16: dates | Leases, task push, create race, tracking, dates | commerce §5 | They are commerce's |

---

## 6. Defects (commerce F12–F19, implemented here)

The commerce PRD owns these numbers:
- F12–F18 map to research §4's WF1–WF10;
- F19 comes from the research's verification pass (research §4, after the table).

All are in code that ships today, so they belong in this PR (K5). This PRD implements them in WM1 (commerce M20), each red first (W7).

| # | Research | Defect | Evidence | Severity | Fixed by |
|---|---|---|---|---|---|
| F12 | WF1 | A refused write-back is recorded as Completed. Every refusal, including a transient owner conflict, answers 207, and the worker counts any 2xx as success | `SyncApply.cs:149-161,171-173`; `SyncActionSubscriptionWorker.cs:75-85`; `tests/MintPlayer.Spark.E2E.Tests/Security/CrossModuleSyncTests.cs:116-134` | Correctness | WX3: per-action outcomes, with the initiator notified |
| F13 | WF2 | Dispatch isn't durable: an `IAfterSave` stores the sync record in a new session after the commit | `ReplicationInterceptor.cs:16`; `SyncActionInterceptor.cs:149-164`; compare `SparkCommittedInterceptors.cs:3-20` | Correctness | The request is stored in the edit's own commit |
| F14 | WF3 | Retries stop after about 5–8 minutes (linear 30 s × attempt, 5 attempts), and Failed is final. The guide claims exponential backoff and "until the owner module registers" | `RetryNumerator.cs:40,46,117-120`; `SyncActionSubscriptionWorker.cs:20`; `SyncActionRetrySweeper.cs:77-79,85-89`; `docs/guide-cross-module-sync.md:117,243,245` | Reliability, docs | A configurable budget with backoff; Failed can be re-driven |
| F15 | WF4 | Retries can reorder writes to one document, so an older update overwrites a newer one. The owner applies to its current version: "the replica's change vector means nothing here" | `SyncActionHandler.cs:43-54` | Correctness (needs a red test) | Per-document ordering plus op ids |
| F16 | WF5 | The consumer's ETL script is deployed verbatim, so `loadDocument` in it pivots past the `Replicate/{Collection}` grant | `EtlTaskManager.cs:65-70`; `EtlDeploy.cs:97-111`; `docs/findings-replication-mtls.md:66,74` | **Security** | Owner-generated transforms (WX4); consumers send no script |
| F17 | WF6 | Module sync ignores attribute rights, so a granted module can overwrite any field | `docs/guide-authorization.md:122`; `SyncActionHandler.cs:132` (`AsSystem()`) | **Security** | Attribute rights apply to module principals |
| F18 | WF7–WF10 | Four problems:<br>• ETL tasks are never removed: no removal path, and an early return with no scripts;<br>• nested values are sent as JSON text;<br>• the guide has drifted from the code;<br>• there is no licence check | • `EtlTaskManager.cs`; `SparkReplicationExtensions.cs:95-96`<br>• `SyncActionInterceptor.cs:184`<br>• `docs/guide-cross-module-sync.md:124,243,245,270-272`<br>• `EtlDeploy.cs:113-115` | Privacy, reliability | WX2/WX4 removal; structured values; a guide rewrite; WX13 |
| F19 | research §4, after the table (no WF id; not yet assessed) | Suspected: the ETL deploy endpoint never checks the request's `TargetDatabase` or `TargetUrls` against the registry. The consumer fills them from its own store, and they become the ETL connection string. So an authenticated module decides where the owner's server sends the granted collection. With F18's never-removed tasks, the flow outlives the module | `EtlDeploy.cs:97-111`; `SparkReplicationExtensions.cs:135-147`; `EtlTaskManager.cs:52-60` | **Security** (suspected; confirm red) | WX4: destinations come from the registry entry of the authenticated module, never from the request |

**How the round-3 design (§4.2, WD13–WD18) fixes each one:**
- F12: per-action answers (200 / 409 conflict / 403-422 refused) replace 207; the consumer records `Refused` and notifies the initiator (§4.2.5).
- F13: the SyncAction and its outbox message are stored in the edit's own session by `ISaveReplacement`/`IDeleteReplacement`.
- F14: the messaging ladder (1 m … 2 h, 48 h), dead-letter → `Failed`, re-drive by custom action.
- F15 and the stale overwrite: `BaseEtag` = the replica's source change vector, one pending action per document, op-id record stored with the apply.
- F16: the owner re-renders the posted manifest and deploys only its own rendering; no script override exists (SPARK068).
- F17: module saves run with attribute rights; a manifest field the module may not read is refused at registration.
- F18: withdrawn/rejected/revoked subscriptions remove their ETL transform or stop pushes; `ISparkLicenseCapabilities` refuses a pinned `Etl` at startup; nested values stay JSON (`element.Clone()`).
- F19: destinations from the registry (already true at e0c542f9, `Endpoints/EtlDeploy.cs:115-129 @e0c542f9`), kept for ETL and pushes; a registry change redeploys.

**Related weakness, without an F number:** the registry trust root (§4.2). Any module can rewrite another module's pinned thumbprint in `SparkModules`. WX4 closes it.

---

## 7. Spikes

**Recording.** Every spike ends with a measured result recorded in §13: numbers, file:line, or a red→green test.

**Environments.**
- Engine and transport spikes follow commerce D24:
  - on the owner's machine, in windows the owner chooses;
  - RavenDB pinned to 3 cores;
  - no feature Community lacks.
- Device spikes run on hardware the owner provides (Q-W21).

**Community checklist.** Commerce S18 gains the warehouse's needs:
- compare-exchange leases (X12);
- cluster-wide transactions on a cluster;
- subscriptions: messaging's one, plus the SyncAction worker only where write-back is used (3 per database allowed);
- no ETL on the HTTP path;
- no revisions;
- no reliance on the expiry sweep;
- ~~at most 12 static and 24 auto indexes per database~~: lifted on 2024-05-22, verify with `/license/status` (§4.5; W19).

| # | Spike | From | Question | Pass criterion |
|---|---|---|---|---|
| W1 | Stock engine invariants | research W1 + WS4 | Does any interleaving break WI1–WI6? Workload: pick confirms, moves, adjustments and counts on hot pick faces; duplicated and retried commands; 50 movers on 10 hot balances | **0 violations** over ≥100 runs × 10k ops, on one node (3 cores) and on a 3-node cluster with cluster-wide transactions. Ledger = balances exactly. Duplicate op ids applied once. p95 pick-confirm ≤150 ms (single node). Retry rate on the hottest balance and its `StockLevel` recorded |
| W2 | Stock feed without oversell | research W2 + WS5 | Does the §4.4 contract hold when replication is late, reordered or duplicated? | Simulator (sales, holds, cancellations, shipments, returns, adjustments; random delay, reordering, duplication):<br>• single channel: **0 oversell** over ≥1,000 runs;<br>• several channels: oversell ≤ other-channel units accepted within the measured lag;<br>• red→green on "refused cancellation of a shipped unit" and "refund of a shipped unit".<br>Real two-app HTTP transport: a warehouse change reaches shop capacity ≤10 s p95 |
| W3 | Release, allocation and batching performance | research W8 | How long do 1,000 orders × 3 lines take over 5,000 locations, with FEFO? | Release + allocate + batch ≤30 s on one node (3 cores) (WD5). Route sequencing of a 50-line batch ≤50 ms |
| W4 | Zone split and consolidation correctness | new (WD5) | Do zone picking and consolidation lose or misplace units? Workload: 1,000 orders over 4 zones, concurrent zone pickers, short picks and cancellations mid-flow | Over ≥100 runs:<br>• every order receives exactly its units (0 lost, 0 misplaced);<br>• no order reaches packing incomplete unless partial shipment is allowed;<br>• no slot is ever assigned to two orders;<br>• every cancellation creates put-backs for all its zone totes |
| W5 | HTTP transport failure modes | research WS1 + new | Does push + reconcile survive failures? Scenarios: consumer down 2 h; producer down 1 h; crash after commit, before the push; consumer crash after apply, before 200; duplicate and out-of-order delivery; a poison payload; a raw write that bypasses the pipeline; an exhausted retry budget | Converges within one reconciliation period. **0 lost and 0 duplicated state changes**, compared by digest. Poison dead-lettered with its reason and an alert. 0 new subscriptions. Run on a Community licence (set by the owner), or on Developer with ETL unused |
| W6 | ETL transport semantics and licence detection | research W3 + WS2 | What does ETL really do, and does WX13 read the licence correctly? | On Developer:<br>• 10k orders → exactly 10k `OutboundOrder`s (natural ids), 0 duplicates after a task reset and redeploy;<br>• only owner-generated transforms deployed;<br>• fate of target-only fields and delete behaviour documented;<br>• multi-source ids don't collide;<br>• ETL keeps flowing while the consumer web app is down;<br>• cancellation round trip ≤5 s p95;<br>• cross-collection ordering documented.<br>On Community: WX13 reports no ETL, `Auto` picks HTTP, and `Etl` refuses startup with an actionable message |
| W7 | Replication defects | research WS3 | Do F12–F19 reproduce, and does the new write path behave? | Each of F12–F19 red, then green. A write-back refusal reaches the initiator and leaves the requester's replica unchanged; an accepted request returns to the requester through the push |
| W8 | Contract genericity | research W9; covers commerce S20 | Can a producer other than the commerce shop drive the warehouse? | A Spark app without commerce packages runs order → ship → return through `Fulfilment.Contracts` alone. The warehouse host has no reference to `Commerce.Checkout` (assembly check). Library diagnostics clean |
| W9 | Keyboard-wedge decoding | research W4 + WS6 | Do wedge scanners deliver exact element strings in Chrome? Devices: Zebra (DataWedge string mode) and Honeywell handhelds, a USB HID scanner on Windows. Test sheet: Code 128 locations, EAN-13, GS1-128 (01)(17)(10), GS1 DataMatrix with GS | Over 1,000 scans per device:<br>• **100% correct** element strings;<br>• no soft keyboard;<br>• scan → UI ≤150 ms p95;<br>• GS survives into the input value, or the documented profile substitutes it;<br>• only the documented profile needed, no per-device code |
| W10 | AZERTY wedge detection | new (WD7) | Can the scan layer tell a US-layout scanner on a Belgian AZERTY host? | The detector flags 100% of 500 garbled scans, and 0 of 500 correct scans and 0 of 200 typed entries. Where `event.code` decoding is on, decoded values match 100%. The setup guide's scanner settings give 100% correct scans |
| W11 | Camera scanning | research W5 + WS6 | Is the camera good enough as a fallback? Devices: a ≤€200 Android (native `BarcodeDetector`), an iPhone (ponyfill), a Windows laptop (ponyfill) | ≥95% decoded within 1.5 s at 15–30 cm for EAN-13 and Code 128 under warehouse lighting. 0 external requests (self-hosted WASM, works offline). Bundle impact recorded, and the decoder lazy-loaded |
| W12 | GS1 parsing corpus | research WS6 | Do the C# and TypeScript parsers agree with GS1? | ≥500 element strings and Digital Link URIs (every AI in the v1 dictionary, FNC1/GS variants, day-`00` dates, symbology identifiers) parse identically in C# and TypeScript and match the GS1 Syntax Engine 100%. Invalid input rejected with the same error class |
| W13 | Offline replay | research W7 + WS9 | Is the disconnect queue safe? Scenarios: a 30-line batch in airplane mode with a conflicting supervisor move; 30 min offline with 200 queued confirmations | Every op applied exactly once after reconnect. Conflicts in the exception list. 0 negative balances (WI1). The queue survives an app restart. `persist()` granted on the installed PWA on Zebra Chrome. Sign-out empties IndexedDB after the warning |
| W14 | Live pick queue at 50 handhelds | research WS8 | What do live task lists cost? | 50 simulated handhelds, 10 task changes/s:<br>• per-change re-evaluation cost and server CPU recorded;<br>• an assignment reaches the right device ≤1 s p95;<br>• 0 deliveries to the wrong user;<br>• behaviour against the per-user socket cap recorded |
| W15 | Label printing paths | research W6 + WS7 | Does each path print quickly and correctly? The same job through the agent, QZ Tray (signed), Zebra Browser Print and the PDF dialog, plus Sendcloud `application/zpl` 203 dpi test labels | Pack-complete → label out ≤3 s p95 for the agent and the bridges (WD7). Printed Code 128, GS1-128 and DataMatrix scan back correctly (W9 scanners). No dialog for the agent and the bridges. A 4×6 PDF prints through a thermal printer's driver |
| W16 | Agent exactly-once printing | new (WD7) | Does a fault ever print a label twice or not at all? 1,000 jobs with injected faults: printer offline, agent killed mid-job, network cut between print and ack, server redelivery, duplicate delivery | **Exactly one physical label per job id** (printer count and agent journal agree). 0 lost jobs. The PDF fallback is offered within 10 s of the agent going offline. The agent reconnects without operator action |
| W17 | PDF library (shared with commerce Q-O9) | new | Which library renders labels, slips, invoices and tickets? Candidates: QuestPDF, PdfSharp/MigraDoc, headless Chromium | The chosen library renders an A6 label, an A4 packing slip, an invoice and a ticket with embedded barcodes; ≤200 ms per label on the server; runs in the Linux container without undocumented native dependencies; its licence puts no revenue limit on hosts. Recorded in both PRDs' logs |
| W18 | Shift sign-in on shared handhelds | new (WD10, WD10b) | Are personal sign-ins fast enough each shift? | On a Zebra Android handheld (Chrome) and a Windows pack-station PC, with an Android and an iPhone as BYOD authenticators:<br>• passkey sign-in over hybrid QR succeeds in ≥19 of 20 attempts per pair, median ≤20 s from tap to signed in;<br>• idle sign-out fires within 5 s of the configured timeout;<br>• sign-out leaves no cached task data |
| W19 | Community index limits | new (licence limits) | **First** read `/license/status` on a Community server: the 12 static / 24 auto caps were lifted on 2024-05-22 (#18564). Run the budget checks below only if a cap still applies; otherwise record the status output and keep only the "0 auto indexes from library pages" check | On the demo app with every warehouse package, Printing and commerce Shipping:<br>• **≤12 static indexes**, with ≥2 left for the host;<br>• after the full E2E suite and sorting and filtering every library grid, **0 auto indexes created by library pages** and ≤24 auto indexes in total;<br>• the server's refusal beyond each limit recorded on Community (error, and whether at deploy or at query);<br>• the WX13 startup warning fires on a fixture that deploys 13 static indexes;<br>• the Q-W26 composable index deploys as one definition |

---

## 8. Production impact (VPS) (K9)

| Change | App | When | Safeguard |
|---|---|---|---|
| Warehouse demo deployment | demo | **Not deployed** (WD11) | Runs locally and in CI E2E. Revisit once the spikes measure VPS headroom |
| Replication redesign (transport, contracts, F12–F19) | Fleet, HR (local demos) | This PR | No production app uses Replication: only `apps/Fleet/Fleet/Program.cs` and `apps/HR/HR/Program.cs` wire it |
| Regenerated `modelHashes.json`, `securityPosture.txt` and route snapshots for the rewired grants | Fleet, HR | This PR | `--spark-verify-model` gates in CI |
| The reference-data app's consumers | reference data (~~live, K8~~ not deployed: K8 revised; developed locally with a basic seed set, commerce D39) and its consumers | When the reference-data PRD adds a consumer | **None required now:** no production app consumes replicas. A future production consumer needs the next two rows |
| A `SparkModules` registry database on `coverage-raven` | VPS | Only with the first production consumer | The operator creates it. Registration's create needs cluster admin and only logs a warning when refused (`ModuleRegistrationService.cs:33-35`). App certificates hold Admin on their own database only (`docs/code-coverage/raven-tls-migration.md:49-54`). WX4's read-only registry mode applies |
| Module mTLS through Traefik | VPS | Only with the first production consumer | `passTLSClientCert` plus `KnownProxies` (`ModuleCertificateForwarding.cs:9-35`), or the internal Docker network |
| Subscriptions per database | VPS | — | The HTTP transport adds none. The SyncAction worker runs only where write-back is used. Community allows 3 per database and 15 per cluster |
| Print agent | on site | — | Runs in the host's warehouse, never on the VPS |
| Backups before production migrations | VPS | commerce §8 | Unchanged by this PRD |

---

## 9. Out of scope (genuinely not being done)

The research's out-of-scope list (research §7), with reasons:
- **Labour management standards and incentive pay.** Enterprise-grade, and they need engineered standards. v1 records the productivity events that would feed them (WD5).
- **Slotting optimisation** (velocity-based re-slotting). It needs months of history, and it is a planning tool. v1 has fixed pick faces and min/max.
- **Yard and dock scheduling, cross-docking, kitting and value-added services, light manufacturing.** These are separate domains. Bundles are handled in the shop, through quota multipliers.
- **Automation:** WCS and WES, conveyors, AMRs, pick-to-light, voice. All hardware-specific. The extension points are the event stream and the task API.
- **Purchasing and supplier management; inventory valuation and costing.** The shop or an ERP owns these. FIFO *allocation* is not FIFO *costing*.
- **3PL multi-client billing** (WD3). The owner dimension itself is in, which settles the research's question of reserving it now.
- **Inter-warehouse transfer orders and multi-site stock balancing.** WD3: no inter-site transfers in v1. Moves inside a site are in.
- **Full 3D cartonisation as the default.** No evidence was found that it beats simple volume rules. The `ICartonizer` seam stays open (Q-W22).
- **Offline receiving, counting and adjusting.** An offline count is an absolute value that conflicts with concurrent movements (research §2; WD6).
- **RFID, and serial capture at receiving.** Serials are captured on the parcel at packing only (WD4).
- **A general route and rule engine** (Odoo's push/pull rules). Costly to build and to explain; a fixed pipeline whose steps can be skipped is enough.

Also excluded by decision:
- **Device enrolment and badge/PIN switching** (WD10).
- **Web Serial and WebUSB** for printers and scales (K11, WD7).
- **Reliance on Background Sync, and new work while offline** (WD6).
- **Native shells and DataWedge intents.** A web page cannot receive intents (research §2), and WD7 chose wedge and camera in the browser.
- **A public deployment of the warehouse demo** (WD11).

---

## 10. Milestones (one PR, inside commerce M20)

These milestones are the content of commerce M20 and ship in the commerce PR (K5).
- **Commits and tests.** Each milestone is committed. Tests run once, at the end, in WM13, which is part of commerce M22 (repo rule).
- **Ordering against commerce:**
  - WM1 must land before commerce M11 builds the fulfilment contract;
  - WM9's scanning and offline entry points must land before commerce M15 builds the scanner PWA.

| # | Milestone | Depends on |
|---|---|---|
| WM0 | Spikes W1–W19 (results logged in §13) | — |
| WM1 | Replication redesign:<br>• WX1–WX4 and WX13;<br>• F12–F19;<br>• Fleet and HR rewired;<br>• replication guides rewritten | W5, W6, W7; commerce X7 |
| WM2 | Shared foundations:<br>• `Fulfilment.Contracts`, `Gs1`, `Warehouse.Abstractions`;<br>• WX8 `Quantity`, WX9 picker, WX11 CSV, WX14 zone-aware schedules | WM1; commerce X4; W12 |
| WM3 | Warehouse core:<br>• sites, zones, locations;<br>• items and pre-items; owner links;<br>• barcode registry and resolution;<br>• stock engine (WI1–WI6); round 5: Recalled lots blocked + recall trace report (GA7), dangerous-goods item data (GC9);<br>• replenishment;<br>• `StockLevel` and the feed;<br>• task envelope; productivity events;<br>• the static indexes of §4.5 (WX16 if Q-W26 is accepted) | WM2; commerce X3, X12; W1, W19 |
| WM4 | Inbound: receipts, put-away, returns and dispositions; round 5: return reasons in `expected-returns` set the default disposition (GA8) | WM3 |
| WM5 | Outbound:<br>• intake and routing;<br>• release and waves; allocation;<br>• discrete, batch, cluster and zone picking; scan-to-confirm;<br>• short picks, backorders, cancellation;<br>• consolidation | WM3; W3, W4 |
| WM6 | Printing:<br>• the job queue and the PDF dialog;<br>• the agent and its protocol;<br>• QZ Tray, Zebra Browser Print;<br>• label rendering (WX6) | W15, W16, W17 (commerce Q-O9) |
| WM7 | Packing:<br>• the pack station;<br>• cartonisation and weighing;<br>• carrier labels, SSCC, serials;<br>• ship-confirm and handover;<br>• round 5: lots per shipment line and the recall trace (GA7), dangerous-goods service restrictions (GC9), customs documents and return labels (GB6, GB5) | WM5, WM6; commerce M11 (Shipping and carriers) |
| WM8 | Counting: ABC plans, blind counts, variance review | WM3 |
| WM9 | Client entry points:<br>• `scanning` (WX5), `offline` (WX7), `printing`;<br>• the warehouse pages;<br>• idle sign-out (WX15) | W9–W14, W18 |
| WM10 | Shop side (commerce work):<br>• `Commerce.Fulfilment` over the contracts;<br>• the capacity source and the stock-feed rules;<br>• the returns gate | WM1, WM2; commerce M11–M12; W2 |
| WM11 | The demo app `apps/Warehouse`:<br>• two-host E2E;<br>• the end-to-end mTLS test | All of the above; commerce M18 |
| WM12 | Guides and READMEs | All |
| WM13 | Final sweep (inside commerce M22):<br>• the test sweep;<br>• browser and device verification;<br>• version bumps | All |

---

## 11. Open decisions

> **All accepted as recommended in Round 2 (owner, 2026-10-17); see §13.** The items below are now decisions.

- **Q-W1 Wave cut-offs in the site's time zone.** Spark Cron runs in UTC, but cut-offs are local wall-clock times. *Recommendation:*
  - sites carry an IANA zone;
  - WX14 adds a core zone-aware schedule that evaluates the cron expression on the local wall clock. A time that DST skips runs at the first valid instant; a time that DST repeats runs once;
  - wave release and count plans use it.
- **Q-W2 The default for cancellation after pick.** The research left `AcceptWithPutBack` versus `Refuse` open; ShipBob refuses once an order is picked. *Recommendation:* `AcceptWithPutBack`. A put-back costs one task; a refused cancellation costs a return.
- **Q-W3 The stock feed across sites.** *Recommendation:* per owner, aggregate over the sites that the routing rules can send that channel's orders to. With one default site, that is that site alone.
- **Q-W4 Channel allotments.** *Recommendation:*
  - ship the shop-side buffer only;
  - W2 measures the multi-channel bound;
  - add allotments (escrow per channel) in this PR only if the measured bound is unacceptable.
- **Q-W5 What counts as sellable.** *Recommendation:*
  - **Count** released lots in storage, pick-face, tote, consolidation, pack-station and outbound staging locations. Picked stock stays on hand until ship-confirm, and excluding it would dip the shop's capacity between pick and ship.
  - **Exclude:**
    - receiving, until put away;
    - quality control and quarantine;
    - returns awaiting inspection;
    - damaged stock;
    - recalled or quarantined lots.
- **Q-W6 Lot substitution at pick.** *Recommendation:*
  - refuse by default;
  - a switch allows swapping the allocation to the scanned lot when that lot is in the same location, released, unallocated, and meets the item's minimum shelf life;
  - the swap is recorded in the ledger.
- **Q-W7 Batches that mix owners.** *Recommendation:* allowed by default, with a switch to forbid. Staff see every line; owner members never see another owner's lines (WI5).
- **Q-W8 ABC class assignment.** *Recommendation:*
  - a nightly suggestion from pick counts over 90 days (about 15% of SKUs carry 85% of turnover, research §1), with manual override;
  - a location takes the class of its fixed item, otherwise the highest class stored there.
- **Q-W9 The source of expected receipts.** *Recommendation:* three sources:
  - manual entry;
  - CSV (WX11);
  - an optional `expected-receipts` contract, so an ERP or the shop can publish ASNs over the same transport.
- **Q-W10 The handover step and the ship-confirm point.** *Recommendation:*
  - an optional handover scan per carrier, with a printable handover list;
  - when it is off, pack-complete is ship-confirm;
  - no carrier manifest API unless an adapter offers one.
- **Q-W11 Staff roles.** *Recommendation:*
  - library security slots that the host binds: receivers, pickers, packers, counters, returns, supervisors. An unbound slot refuses startup (commerce §4.13), which here is the point: the host must decide who picks;
  - owner-organization members get read-only, owner-filtered views;
  - Organizations custom roles (commerce X8) are not needed for staff, who are the operator's employees.
- **Q-W12 May a pre-item's SKU change when it is linked?** *Recommendation:* yes. The link re-keys the (owner, SKU) unique key and is audited. Stock and ledger keep pointing at the unchanged item id.
- **Q-W13 Idle sign-out with an unsent offline queue.** WD10 wants idle sign-out, while WD10b wipes the queue at sign-out; together they would silently drop scans. *Recommendation:*
  - while unsent commands exist, idle **locks** the device instead of signing out. The same user re-authenticates to resume, and the queue is kept;
  - queues are partitioned per user: another user on the device never sees or replays them;
  - an explicit sign-out warns, then wipes.
- **Q-W14 The print agent's credential and distribution.** *Recommendation:*
  - **Credential:** no new scheme. A dedicated agent principal, holding only agent rights, gets a Spark personal access token (`libs/authorization/MintPlayer.Spark.Authorization/Agents/PersonalAccessTokens.cs`), paired with a one-time code shown in the app.
  - **Distribution:** a `dotnet tool` that installs itself as a Windows service or a systemd unit.
  - **Monitoring:** the app shows each agent's version and heartbeat, and warns when an agent is outdated.
- **Q-W15 The Zebra Browser Print library licence.** The library is "available on request", and its licence is unverified (research §2). *Recommendation:*
  - never bundle it; the host supplies its URL;
  - the adapter codes against its API;
  - check the licence before release.
- **Q-W16 GS1 parsing: wrap the Syntax Engine or port its dictionary?** *Recommendation:* port the dictionary:
  - generate both parsers from GS1's AI dataset, kept as data: C# in `MintPlayer.Spark.Gs1`, TypeScript in `scanning`;
  - test them against the Syntax Engine's vectors (W12);
  - this keeps a native binary out of the NuGet package and a second large WASM out of the camera bundle.
- **Q-W17 ETL transforms from contracts.** **Superseded by WD14 (round 3):** the transform is generated from the replica class at build time and re-rendered by the owner (§4.2.1–4.2.2). The original recommendation, accepted in round 2:
  - contracts declare a projection the owner can express both as C# (HTTP path) and as a generated ETL script (field selection and renames);
  - a contract whose projection needs code is HTTP-only, and startup says so;
  - never consumer JavaScript (WD1).
- **Q-W18 Editing a replica: what the editor sees.** (Unchanged by round 3; a Conflict now also counts as a refusal, with "reload and edit again".) *Recommendation:*
  - the save returns "sent to {owner}" through commerce X7's `ISaveReplacement`;
  - the form shows a pending badge until the push lands;
  - a refusal arrives as a notification, with its reason.
- **Q-W19 Productivity data is employee data.** *Recommendation:*
  - record events per user, because the metrics need it;
  - per-user views for supervisors only, aggregates for everyone else;
  - a retention period set by the host;
  - included in the GDPR export and deletion hooks;
  - the guide warns that monitoring employees may require consultation under national labour law, for example with works councils.
- **Q-W20 MCP access to warehouse writes.** *Recommendation:* as commerce default 11. Write actions are not exposed to MCP agents unless the host enables them.
- **Q-W21 Hardware for the device spikes.** W9–W11, W15, W16 and W18 need real devices. *Recommendation:*
  - one Zebra Android handheld (DataWedge), plus a Honeywell if available;
  - a USB HID scanner on a Windows PC with the Belgian AZERTY layout;
  - a 203 dpi Zebra desktop thermal printer;
  - a phone priced at €200 or less, running Android;
  - an iPhone.
- **Q-W22 Adapters the research named that no decision covers:**
  - RawTcp (a server on the LAN);
  - PrintNode;
  - Zebra Link-OS Weblink (Java SDK only);
  - 3D cartonisation (for example 3DContainerPacking, MIT).

  *Recommendation:* ship none of them. The agent covers raw TCP, and the public `IPrintAdapter` and `ICartonizer` interfaces let a host add the rest (K7).
- **Q-W23 Owner identity across apps.** Organization ids differ per database. *Recommendation:*
  - an owner link maps (source module, external owner id) to a warehouse owner organization;
  - a supervisor creates it; a single-owner pair gets it automatically;
  - orders from an unlinked owner are `Rejected(OwnerNotServed)`;
  - the link also filters which stock levels a module receives.
- **Q-W24 The transport in demos and tests.** Local runs and CI hold the Developer licence, where `Auto` picks ETL. *Recommendation:*
  - demos and E2E pin `Transport=Http`, the production path;
  - the replication E2E runs a second time with `Etl` on CI's Developer licence.
- **Q-W25 Defaults the owner hasn't set.** *Recommendations:*
  - the offline queue on (`AssignedTasksOnly`), since WD6 frames it as the operating mode;
  - an offline warning after 1 h, then daily;
  - idle sign-out after 15 min hidden or inactive;
  - a stock-feed flush every 2 s;
  - replica reconcile hourly; stock reconcile daily at 03:00 site time.
- **Q-W26 One documents index across three packages.** `Warehouse_Documents` must map document types from Inbound, Outbound and Packing. Core cannot reference those packages, and a C# multi-map index class can only map the types it references. Without a fix, the plan needs one index per package: 11 of the 12 static indexes, leaving 1 for the host (§4.5). *Recommendation:*
  - a generic core seam (WX16): packages contribute maps to a named index, and the core assembles them into one multi-map definition at startup;
  - W19 verifies it on Community;
  - the alternative is to keep these document types in core and only their behaviour in the packages.
- **Q-W27 The approval record: own persistent object or `libs/approvals`?** `MintPlayer.Spark.Approvals` already has kinds, deciders, Revoke, Stale and notifications (`libs/approvals/MintPlayer.Spark.Approvals.Abstractions/ApprovalKinds.cs:25`; `ISparkApprovals.cs:20`). *Recommendation:* the replication library's own `SparkReplicationSubscription` persistent object with `ApproveReplication`/`RejectReplication` custom actions (the owner's list: persistent objects, custom actions, security.json, model sync, messaging), so replication, which every multi-app host needs, doesn't pull in Approvals; notify approvers through `ISparkNotifier` when Notifications is present. **Decided (owner, 2026-10-20): the replication library's own persistent object, no dependency on `libs/approvals`.** Built as `SparkReplicationRequest` (the program unit "Replication requests") plus internal `SparkReplicationSubscription` bookkeeping (§4.2.2); approvers are told through open pages (live query) rather than `ISparkNotifier`, which replication does not reference.
- **Q-W28 A second edit while one is pending.** *Recommendation:* refuse it locally ("an edit of this document is pending at {owner}"). It would carry the same stale base and be a Conflict at the owner anyway; refusing it early keeps per-document order without sequences (F15). **Decided (owner, 2026-10-20): refused locally, "a change to this row is still pending at {owner}".** Built with `SyncPendingMarker` (§4.2.5).
- **Q-W29 A consumer upgraded with a new manifest, before approval.** *Recommendation:* the consumer keeps accepting pushes made with its last **approved** hash (missing fields keep their defaults), and the owner keeps the old ETL transform running until the new one is approved; data stays fresh, and the new fields arrive after approval through the initial reconcile. The alternative, refusing everything until approval, freezes replicas for as long as nobody approves. **Decided (owner, 2026-10-20): as recommended, an upgraded consumer keeps receiving pushes in its last approved shape until the new manifest is approved.** Built: the apply endpoint accepts the acknowledged `ApprovedHash`; the owner keeps delivering the approved manifest (§4.2.3).

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| The Developer licence locally and in CI hides production behaviour (ETL works locally, not in production) | Tests pin `Http` and run once more with `Etl` (Q-W24). WX13 refuses `Etl` on Community. Commerce S18 checklist |
| The HTTP transport loses or duplicates state under failures | After-commit work stored in the save's commit, change-vector guard, tombstones, owner reconcile. W5 |
| Change-vector comparison is wrong on a multi-node owner (concurrent vectors) | `Concurrent` is never applied, only reconciled; `SparkChangeVector.Compare` unit tests over real vectors from a 3-node test cluster fixture (W6) |
| The C# manifest projector and the generated ETL script drift apart | One shared renderer source file; an equivalence test runs both on the same documents under the Developer licence (W6) |
| An approval nobody makes blocks a production deploy | Q-W29 keeps the approved shape flowing; the pending request notifies approvers |
| Oversell across channels during replication lag | Shop-side buffer; W2 measures the bound; allotments if needed (Q-W4) |
| Contention on hot balances or on the `StockLevel` aggregate | OCC retry; W1 measures it; split the aggregate per channel if W1 shows a hot spot |
| Stale indexes mislead allocation | Candidates come from queries; claims are made on loaded documents (§4.5) |
| ~~The composed app exceeds Community's index limits (12 static, 24 auto per database)~~. **Corrected:** caps lifted on 2024-05-22 (#18564); residual risk only for a licence that never refreshed | A budget per library (§4.5: 9 of 12 planned, 3–4 left for the host); multi-map indexes; library pages bound to static indexes; the WX13 startup warning; WX16 (Q-W26); W19. Commerce carries the same risk under S18 |
| Wedge garbling (AZERTY, GS bytes) at pack stations | W9, W10; detection, `event.code` decoding, setup guide |
| Camera decoding too slow on low-end devices or iOS | W11. Wedge handhelds are the volume path |
| Duplicate or lost labels | Agent journal plus acknowledgement; PDF fallback. W16 |
| PDF library licence constraints | W17, shared with commerce Q-O9; prefer MIT |
| Safari storage eviction or sign-out wipes unsent scans | Warnings (WD6), idle lock (Q-W13), `persist()` |
| Cross-owner leaks | WI5, asserted in tests everywhere (WD3) |
| Hybrid passkey sign-in unsupported on some handhelds | W18; password and external sign-in remain |
| Legal exposure of employee monitoring | Q-W19 |
| Two-host E2E infrastructure not started (multi-host H1, H2, R2) | WM11 builds it, with one `SparkModules` database per fixture |
| PR size and duration | Accepted (K5); milestones committed separately; tests batched at the end |
| Windows MAX_PATH | Short folder names (D31) |

---

## 13. Decision log

Owner answers are quoted where they changed or refined the recommendation.

### Inherited from the commerce grilling (commerce §13)

- **D12:** Two web applications that consult each other's data (quoted in §1). This is the reason for §4.2 and §4.3.
- **D13:** The warehouse plugin goes in the same PR, under this separate PRD. The contract is designed against the real consumer, kept generic, and proven with a stub (W8).
- **D14:** Sendcloud, bpost, PostNL and a test carrier. Owner: "Not up to us to decide here. The platform hoster should be able to freely decide this." (K7)
- **D31:** Generic packages flat, short folders under `libs/`, permanent aliases (§4.1).
- **I4 exception**, applied to commerce §4.3 from this research: a refund of shipped goods returns no quota; a restock does.

### Round 1 (owner, 2026-10-17)

- **WD1 (WQ1, transport):** Push + reconcile becomes **Spark's standard replication transport**. ETL stays as an optional transport behind a licence check. Fleet and HR are rewired. Closes F16 (no consumer scripts). Also serves reference-data consumer copies. The design:
  - ~~config-declared, versioned C# projection contracts with several sources, granted as `Replicate/{contract}`;~~ (superseded by WD13, round 3)
  - ~~a version stamp per save;~~ (superseded by WD18: the source change vector)
  - the outbox through `IAfterSaveCommitted`, in the save's commit;
  - messaging push over module-certificate HTTPS;
  - newer-version-wins read-only replicas, with tombstones and a durable `IReplicaChanged<T>`;
  - a cron digest reconcile for the initial load and repair.

  Owner: "WQ1: Good, let's pick A". *Evidence:* no ETL on Community (`docs/endpoints_generator_adoption_plan.md:219`; `docs/multi-host-e2e-plan.md:130-132`); the options are compared in research §5.
- **WD1 refined (owner):** "Probably if ETL isn't available. So if ETL is available, the user can use the ETL replication - otherwise the updater module sends a syncaction request to the owning module, and the owning module sends the http-request (etl not available) to all other modules - all using the ClientCertificate authentication for cross-module webrequest communication". → The transport is chosen by licence capability:
  - ETL when the licence has it;
  - otherwise a non-owner sends a SyncAction request to the owner (the existing write-back path, with F12–F15 fixed); the owner applies it, then pushes the new state over HTTPS to every consumer module;
  - every cross-module call uses module client certificates (`AddModuleCertificateAuthentication`, `docs/guide-replication-mtls.md`);
  - the cron digest reconcile stays on the HTTP path (initial load, repair of expired pushes) and isn't needed with ETL;
  - write-back stays a generic feature, which shop ↔ warehouse doesn't use (WD2).

  The reading of "all other modules" as including the requester is recorded in §4.2. *Evidence:* write-back works on Community (`Models/SparkSyncAction.cs:7`); module mTLS exists (`ModuleCertificateAuthentication.cs:49`).
- **WD2 (WQ2, write direction):** State replication both ways, one owner per fact; **no app writes the other's documents**.
  - The shop publishes fulfilment orders, with cancel requests as a status.
  - The warehouse publishes:
    - progress: accepted, picked, packed, shipped with tracking, cancel refused;
    - stock levels per SKU and channel;
    - return receipts.
  - Each side reacts through `IReplicaChanged<T>`. Derivation is idempotent and order-insensitive ("packed" after "shipped" is a no-op).
  - Cancellation is a request answered by accept or refuse.

  *Evidence:* research §6 (Shopify's fulfilment request states, ShipBob's cancel-before-pick). Module sync bypasses attribute rights today (`docs/guide-authorization.md:122`), so writing into the other app's documents would expose every field.
- **WD3 (WQ3, scope):**
  - **Multi-owner:** the owner is an Organization on stock, balances and ledger, with a row filter.
  - **Multi-site:** each site has its own zones and locations. Orders are routed by configuration rules or to the owner's default site.
  - **No inter-site transfers in v1; 3PL billing out.**
  - The owner filter is asserted in tests everywhere, and cross-owner pick lists must not leak other owners' documents.

  *Evidence:* research §1 (Odoo's owned stock; no primary source on 3PL billing); `IOrganizationOwned.cs:25-29`.
- **WD4 (WQ4, tracking):** Lots and expiry (FEFO); **serials captured on the parcel at packing only**.
  - A balance document per (location, SKU, lot), with a `DateOnly` expiry.
  - Allocation FIFO, FEFO or closest, per owner and SKU.
  - GS1-128 AI (10)/(17) parsed at receiving.
  - Pick confirmation checks the scanned lot against the allocated lot.

  *Evidence:* research §1 (Business Central's FEFO tie-breaks, Odoo's removal date); `SparkModelShape.cs:206`.
- **WD5 (WQ5, processes):** Option A **plus zone picking**; the owner chose beyond the recommendation. Option A:
  - receiving (expected or blind) and put-away rules (fixed, same lot, nearest empty);
  - an append-only ledger and balances, reason-coded adjustments, blind ABC cycle counts (30/60/120 days), min/max top-up;
  - release immediate or by cron waves at cut-offs;
  - FIFO, FEFO or closest allocation;
  - discrete, batch and cluster picking (totes) by walk sequence, scan-to-confirm, short picks that zero and re-allocate, backorders, the cancellation handshake;
  - a pack station (scan verification, rule-based cartonisation, carrier label through the commerce carrier adapters, optional SSCC);
  - returns receipt, inspection and disposition, feeding the I4 exception;
  - productivity events.

  Zone picking adds pickers per zone, orders split across zones, and consolidation at a put wall or consolidation station. Spikes: allocating 1,000 orders × 3 lines ≤30 s (W3); zone split and consolidation correctness (W4). *Evidence:* research §1 (picking is up to 55% of operating cost; the pick-method taxonomy; walk sequence); research §7.
- **WD6 (WQ6, offline):** Online first, with a disconnect queue for **assigned** tasks:
  - tasks cached on the device;
  - offline scans in IndexedDB, with the device time and an op id;
  - replay in order on the standard `online` event and a manual "sync now". No reliance on Chromium-only Background Sync (K11);
  - the server applies idempotently by op id; conflicts become picker exceptions (put-back or recount);
  - no new work offline; receiving and counting online only;
  - a warning after a configurable time offline (Safari's 7-day storage wipe).

  It shares `ng-spark/offline` and `ng-spark/scanning` with the ticket scanner. *Evidence:* research §2 (Background Sync support, storage eviction, bounded-counter theory).
- **WD7 (WQ7, hardware):** Keyboard-wedge handhelds and cameras, through the shared `ng-spark/scanning`: `BarcodeDetector` plus a same-shape WASM ponyfill, with AZERTY garble detection and a setup guide (spiked).
  - **Printing** through `MintPlayer.Spark.Printing` adapters (K7):
    - the PDF print dialog (default);
    - an on-site .NET print agent: an outbound connection to the app, ZPL or raw output to LAN printers, and a second deployable with install, update, device authentication and monitoring;
    - QZ Tray;
    - Zebra Browser Print.
  - A PDF fallback when the agent is down. Spike: a label in ≤3 s, exactly once on retries.
  - **Scales:** manual entry or through the agent.
  - **No Web Serial or WebUSB** (K11).

  *Evidence:* research §2 (the `BarcodeDetector` support matrix; browsers cannot open raw sockets; the bridge options).
- **WD8 (WQ8, carriers and barcodes):** **Whoever packs ships:**
  - the warehouse creates the label at the pack station and receives the tracking webhooks;
  - the shop learns through the shipment replica;
  - a shop without a warehouse app uses the same adapters;
  - a carrier account per owner, or the site's default;
  - after an ownership switch, parcels the shop labelled keep reporting to the shop.

  Plus, owner: "make sure the vendor barcodes and warehouse barcodes cooperate nicely (when multiple articles are know for a specific barcode, the user should be able to select the correct one)" → a shared barcode resolution pipeline (§4.6). *Evidence:* research §2 (Sendcloud v3 label formats; GS1 AIs and symbology identifiers); research §6 (webhook dedupe).
- **WD9 (WQ9, item master):** The shop owns sellable items; the warehouse extends them:
  - a read-only replica, plus warehouse-owned extension documents: dimensions, weight, packaging levels and barcodes, storage requirements, lot and expiry policy, default locations, min/max;
  - identity (owner, SKU);
  - warehouse-owned items for goods nobody sells;
  - pre-launch pre-items, linked later **without moving stock or splitting history**;
  - a discontinued shop product becomes a tombstoned replica while stock remains.

  *Evidence:* research §6 (Shopify fulfilment services, ShipBob item sync).
- **WD10 (WQ10, sign-in):** **Full personal sign-in on every device, each shift.** The owner chose this over enrolled devices with badge and PIN.
  - No new credential scheme, no device enrolment.
  - Friction is softened with K11 standard APIs: passkeys where an authenticator exists, WebAuthn cross-device (hybrid, QR) sign-in with the operator's own phone, idle auto sign-out (Page Visibility).
  - The owner later said a free-text message "was an answer to a 6 questions ago". It is recorded as WD1 refined, and WQ9 stays A.

  *Evidence:* passkey sign-in exists (`PasskeySignIn.cs:25`); no idle sign-out exists (§4.10).
- **WD10b (owner):** "since you mention passkeys, employees could also be able to use their smartphone" → BYOD phones are supported:
  - (a) as passkey authenticators, through WebAuthn cross-device sign-in on shared handhelds;
  - (b) as the scanning device itself: camera scanning, PWA install (WD7).

  BYOD data minimisation: sign-out clears the cached tasks and the unsent offline queue (with a warning), and no customer address is kept beyond the current task.
- **WD11 (WQ11, deployment):** **Demos stay local** (commerce and warehouse); ~~only the reference-data app goes live. Revisit after the spikes measure VPS headroom.~~ *(Superseded: K8 revised the same day, so the reference-data app is not deployed either (commerce D39, round 5: developed locally with a basic seed set); the VPS-headroom spikes S2/S4 were dropped in commerce round 4.)* Closes commerce Q-O5. Commerce gained Q-O9 (PDF rendering), shared with WX6 and W17. *Evidence:* about 3 GB of RAM is free on the VPS (commerce §8).
- **WD12 (WQ12, returns gating; decided as a default, no question asked):** For warehouse-backed stock, refunds for returned goods wait for the warehouse's receipt **by default**. The host can switch to refund on request (commerce §4.9). *Evidence:* research §6: EU withdrawal law lets the trader withhold the refund until the goods are received (unverified; Directive 2011/83/EU art. 13(3)). *(Round 5, commerce GA1: the same article also ends the withholding at the buyer's proof of dispatch, whichever comes first; still to be confirmed on EUR-Lex, commerce D37. §4.3 updated.)*
- **Constraints restated for this PRD:**
  - **K9:** "A redesign would be allowed too, but we'll probably need to change things on the vps then" → §8.
  - **K10:** "Now we only really need to bother with timezones in one place - all the complex timezone logic can be offloaded to core-framework shipped built-ins" → §4.14, WX14.
  - **K11:** "Anything that can be done through the W3 browser-api's should probably be done so" → §4.8–§4.10. Web Serial and WebUSB are excluded as Chromium-only drafts.

### Round 2 (owner, 2026-10-17)

- **Q-W1 … Q-W26:** all §11 recommendations **accepted as written** ("Accept all"); Q-W17 later superseded by WD14 (round 3). §11 now records decisions, not open questions:
  - zone-aware cron for site-local cut-offs (WX14);
  - accept-with-put-back after pick;
  - a per-owner feed over routable sites;
  - a buffer only, with allotments only if W2 shows the bound is unacceptable;
  - the sellable-location rule;
  - lot substitution refused by default;
  - mixed-owner batches allowed;
  - nightly ABC suggestions;
  - manual, CSV and contract ASNs;
  - an optional handover scan;
  - staff slots the host binds;
  - pre-item SKU re-key;
  - idle locks rather than signing out while scans are unsent;
  - the agent on a personal access token and distributed as a `dotnet tool`;
  - Zebra Browser Print never bundled;
  - GS1 parsers generated from GS1's dataset;
  - ETL scripts generated from simple contracts;
  - the "sent to {owner}" replica-edit UX;
  - employee productivity data with GDPR handling and a works-council note;
  - no MCP writes by default;
  - **the owner provides the device-spike hardware (W21)**;
  - no extra adapters;
  - owner links across apps;
  - HTTP pinned in demos and E2E, plus an ETL E2E on CI;
  - the W25 defaults;
  - the shared-index seam (WX16).
- **Index limit:** owner asked "What's the 12-index limit?" → answered from ravendb.net/buy's footnotes (12 static + 24 auto per database). **Correction, same day:** RavenDB lifted those caps for Community on 2024-05-22, and existing licences update when the server reaches api.ravendb.net ([discussion #18564](https://github.com/ravendb/ravendb/discussions/18564)); the buy page is stale. The decisions stay valid (the shared index, WX16/X19, is the cleaner design anyway). The hard budget becomes "verify with `/license/status`" (W19).

### Round 3 (owner, 2026-10-20): replication redesign

The interrupted WIP commit b4c218eb renamed `[Replicated]` to `[Replica]` and kept per-type contract records (`apps/HR/HR.Library/Contracts/PersonV1.cs`, `CompanyV1.cs`, `apps/Fleet/Fleet.Library/Contracts/CarV1.cs`, added in 43d492df). The owner rejected that: one hand-written record per replicated type duplicates what the replica class already states, against Spark's low-code, don't-copy principle. Binding decisions (owner instructions, 2026-10-20, relayed to the design pass):
- **WD13:** keep `[Replicated]` (no rename); the replica class is the single declaration; no contract records.
- **WD14:** the script is source-generated as `const string`s into the `partial` replica class; no runtime reflection. The design pass chose: attribute arguments `SourceCollection` (required), `SourceModule` (optional for generic libraries), `OriginalType`, `WriteBack`, and `[ReplicatedFrom]` per property; a non-partial class is SPARK065 (error, with a code fix); no hand-written script override (SPARK068), because a consumer-authored script is F16.
- **WD15:** hashed on both sides: no post of an unchanged script, no redeploy of an unchanged approved one.
- **WD16:** the owner approves, from Spark features; auto-approval only when the **owner's** host environment is Development, never by build configuration; destinations from the authenticated module's registry entry (F19).
- **WD17:** with ETL, deploy the approved script as an ETL task; without ETL (Community, production), the owner delivers the projection itself over Spark messaging and the mTLS channel, write-back shares that plumbing, and a Spark cron job reconciles. The design pass chose an owner-side C# manifest projector over a RavenDB JavaScript `select` query (RQL splicing; consumer-shaped JavaScript) and over a generated owner projection (the owner doesn't reference consumer types), and no new subscription.
- **WD18:** write-back reuses Spark's optimistic concurrency with the replica's source change vector as `BaseEtag`; pushes never apply an older projection.
- F12–F19 stay in scope (§6).

**Evidence, reference system** (a decompiled enterprise module-replication system that Spark's design is modelled on; summary recorded 2026-10-20):
- The consumer computes SHA-512 over `{database, collection, script}` and posts only when the hash changed, through a durable outbox with retry.
- The hub stores a job per request and auto-approves and deploys only when its host environment `IsDevelopment()`; otherwise an administrator approves through a custom action that shows a diff against the last approved job. Approval state is `job.Approved` plus a deployed-task record per database.
- SyncActions: a durable outbox; retry backoff 1 m / 5 m / 30 m / 2 h; give up after 48 h, delete after 24 h more; deletes propagated by an `OnBeforeDelete` hook; a `Source` stamp to ignore its own echo (needed because it saves the replica locally first; Spark doesn't, §4.2.5).
- **Its flaw, not copied:** a build-configuration switch (`#if DEBUG`) forces the Development environment and so auto-approves.
- Not copied either: the database inside the hash (Spark's destination comes from the registry, so a registry change redeploys instead).

**Evidence, this repository** (file:line in §4.2):
- stale overwrite: the owner applies on the version stored now, `libs/spark/MintPlayer.Spark/Services/SyncActionHandler.cs:43-53,63 @e0c542f9` and `:39-47,60` (working tree);
- the concurrency to reuse: `PersistentObject.Etag` is the change vector (`libs/spark/MintPlayer.Spark/Actions/DefaultPersistentObjectActions.cs:197-199`), saves store with the expected vector (`DatabaseAccess.cs:862,906-907`), `ISparkReplacedSaves` takes `BaseEtag` (`SparkSaveReplacement.cs:59-63,90-105`);
- durable capture without a subscription: after-commit work is stored in the save's session (`SparkCommittedInterceptors.cs:104-114`);
- subscription budget: messaging uses one (`SparkMessagingOptions.cs:195-212`), the SyncAction worker a second (`Workers/SyncActionSubscriptionWorker.cs:36`); round 3 removes the second;
- generator precedent: `ValueObjectKeyGenerator` (SPARK016 for a missing `partial`, `ValueObjectKeyReporter.Rules.cs:23-30`); next free id SPARK065.

Q-W27, Q-W28 and Q-W29 were decided by the owner on 2026-10-20 (below).

**Final decisions (owner, 2026-10-20, relayed to the WM1 implementation).** Evidence: owner decision; implementation file:line in §4.2.
- **Q-W27:** the approval record is the replication library's own persistent object, no dependency on `libs/approvals`.
- **Q-W28:** a second write-back edit while one is pending is refused locally ("a change to this row is still pending at {owner}").
- **Q-W29:** an upgraded consumer keeps receiving pushes in its last approved shape until the new manifest is approved.
- **SourceModule optional** on `[Replicated]`; fallback `Spark:Replication:Sources:{TargetCollection}`; startup refuses when neither names a source.
- **WD19, per-property compare-and-set** replaces the whole-document `BaseEtag` for write-back (WD18 struck for write-back; the change vector still guards pushes). Spark's concurrency vocabulary is reused: `SparkConflictException` became the public base of the internal `SparkConcurrencyException` (`libs/spark/MintPlayer.Spark.Abstractions/SparkConflictException.cs`), the endpoints catch the base, and the 409 body gains `attributes`.
- **WD20, user feedback:** synchronous call within the save (5 s, configurable), queued only when the owner does not answer; deferred terminal outcomes recorded per user (durable), pushed on the private live topic `replication:outcomes`, shown at next sign-in ("My pending changes"), GaveUp mailed when MailManager is registered; a pending badge on the row; never applied locally.
- **Reuse the message queue** (`IMessageBus`, `IMessageOutbox`, recipients, `RetryAfterException`, budgets, dead-letter) for registration, deploys, pushes, reconciles and queued write-backs, on the one queue `spark-replication`; replication creates no RavenDB subscription and no hosted service.
- **WD21 (owner addition, 2026-10-20):** approval from the website through the library's program unit "Replication requests" (pending first; consumer, source, state Pending/Approved/Rejected/Superseded, requested vs approved hash, readable diff; Approve/Reject custom actions guarded by `security.json`, default Administrators through the `replication:approvers` slot, hidden from users without the read right; auto-approved rows marked); wired into HR and Fleet.

**Implementation decisions the owner's instructions did not dictate (WM1, 2026-10-20):**
- Two documents for the gate (request = persistent object per shape, subscription = delivery state), so history and "Superseded" exist without a revision feature.
- `TargetCollection` attribute argument with a generated Inflector-like default, verified against the store's conventions at startup (the generator cannot run RavenDB's pluraliser).
- The ETL script's `Spark-Replica-Source-Module` is added by the owner at deploy (`RenderEtlScript`); the posted script and hash are module-independent, so one generated constant serves every owner.
- A delete of a write-back replica is sent like an edit whose bases are every writable field (a row changed meanwhile conflicts); the local delete becomes an unchanged save (`IDeleteReplacement`) and the row disappears with the owner's tombstone.
- The pending mark after an applied edit lasts until the replica's own change vector moves (works for ETL too, where no apply endpoint runs), with a 1 h expiry.
- The retry ladder is derived from the elapsed time (no stored counter; `RetryAfterException` parks are not messaging attempts); messages get a generous `MaxAttempts` and the recipient decides when to give up.
- ~~The capture interceptor claims every non-replica model type: one small after-commit work document per save on a module that runs replication.~~ Superseded (owner, 2026-10-20: "y" to narrowing it): it claims only model types whose collection `security.json` grants `Replicate/{collection}` on (`ReplicaCapture.cs`, `ReplicaPublishInterceptor.AppliesTo`), since the register endpoint refuses any other. `AppliesTo` is cached per type, so a grant added by a security reload is captured after the next restart; the reconcile carries it until then.
- The reconcile holds the consumer digest in memory instead of merge-joining two id orders.
- Owner-side `IReplicaChanged<T>` and outcome observers (`ISyncActionOutcomeObserver`) are DI extension points; replication does not reference Notifications.

---

## 14. Verification and acceptance

**Invariants and contracts.**
- **WI1–WI6 hold:** W1 passes, and a scaled-down concurrency guard stays in the regular suite.
- **The stock feed holds:** W2 passes, and a scaled-down simulator test stays in the suite.
- **The transport holds:** W5's failure modes pass. The replication E2E runs under `Http` and again under `Etl` (Q-W24). Commerce S18's checklist covers the warehouse.
- **F12–F19** go red→green (W7).
- **Index limits:** `/license/status` read on production and recorded; if a cap applies, W19's budget checks pass, and a test covers the WX13 startup warning.

**E2E on the demo pair** (commerce demo + `apps/Warehouse`, two hosts):
- order → accept → wave release → zone pick and consolidation → pack with a test-carrier label (PDF) → shipped → the shop shows the tracking;
- cluster pick with a short pick → re-allocation → backorder → receipt → allocation → shipped;
- cancellation accepted before pick; accepted with a put-back after pick; refused after shipping, then turned into a return;
- return received and restocked → refund released (WD12) → capacity rises, and no quota returns on the refund;
- round 5: a `NonConformity` return defaults to Inspect → Quarantine; a withdrawal with proof of dispatch releases the refund before receipt, and the quota still rises only at restock (GA1, GA8);
- round 5: a lot set to Recalled is blocked from allocation, and the recall trace lists the shipments and fulfilment orders that carried it, which the shop resolves to buyers (GA7);
- round 5: a lithium-battery item is never offered a carrier service that refuses it (GC9);
- offline replay with a conflict landing in the exceptions (scaled-down W13);
- a barcode with two matches → the user chooses; an unknown barcode → link to item;
- owner isolation: a member of owner A sees nothing of owner B in any list, pick list, search or export;
- consumer down, producer down, and a restart during a push (scaled-down W5);
- the stub producer end to end (W8).

**Fleet ↔ HR.** The rewired replication runs end to end, including a write-back refusal shown to the initiator. The missing end-to-end mTLS test is added (`CrossModuleSyncTests.cs:24-31`).

**Browser checks** through the Playwright MCP tools:
- handheld pages at phone width;
- the pack station;
- the barcode-choice dialog;
- the offline banner.

**Device checks** on real hardware: W9–W11, W15, W16 and W18, recorded in §13.

**Versions:**
- new packages start at `11.0.0-preview.1`;
- every touched `libs/` csproj gets a preview bump;
- `@mintplayer/ng-spark` gets one minor bump, shared with commerce;
- no major changes (repo rule).
